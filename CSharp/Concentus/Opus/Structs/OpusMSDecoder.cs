﻿using Concentus.Common;
using Concentus.Common.CPlusPlus;
using Concentus.Enums;
using Concentus.Structs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Concentus.Structs
{
    public class OpusMSDecoder
    {
        internal ChannelLayout layout = new ChannelLayout();
        internal OpusDecoder[] decoders = null;

        private OpusMSDecoder(int nb_streams, int nb_coupled_streams)
        {
            decoders = new OpusDecoder[nb_streams];
            for (int c = 0; c < nb_streams; c++)
                decoders[c] = new OpusDecoder();
        }

        #region API functions

        internal int opus_multistream_decoder_init(
      int Fs,
      int channels,
      int streams,
      int coupled_streams,
      Pointer<byte> mapping
)
        {
            int i, ret;
            int decoder_ptr = 0;

            if ((channels > 255) || (channels < 1) || (coupled_streams > streams) ||
                (streams < 1) || (coupled_streams < 0) || (streams > 255 - coupled_streams))
                return OpusError.OPUS_BAD_ARG;

            this.layout.nb_channels = channels;
            this.layout.nb_streams = streams;
            this.layout.nb_coupled_streams = coupled_streams;

            for (i = 0; i < this.layout.nb_channels; i++)
                this.layout.mapping[i] = mapping[i];
            if (OpusMultistream.validate_layout(this.layout) == 0)
                return OpusError.OPUS_BAD_ARG;

            for (i = 0; i < this.layout.nb_coupled_streams; i++)
            {
                ret = this.decoders[decoder_ptr].opus_decoder_init(Fs, 2);
                if (ret != OpusError.OPUS_OK) return ret;
                decoder_ptr++;
            }
            for (; i < this.layout.nb_streams; i++)
            {
                ret = this.decoders[decoder_ptr].opus_decoder_init(Fs, 1);
                if (ret != OpusError.OPUS_OK) return ret;
                decoder_ptr++;
            }
            return OpusError.OPUS_OK;
        }

        public static OpusMSDecoder Create(
              int Fs,
              int channels,
              int streams,
              int coupled_streams,
      Pointer<byte> mapping,
      BoxedValue<int> error
)
        {
            int ret;
            OpusMSDecoder st;
            if ((channels > 255) || (channels < 1) || (coupled_streams > streams) ||
                (streams < 1) || (coupled_streams < 0) || (streams > 255 - coupled_streams))
            {
                if (error != null)
                    error.Val = OpusError.OPUS_BAD_ARG;
                return null;
            }
            st = new OpusMSDecoder(streams, coupled_streams);
            ret = st.opus_multistream_decoder_init(Fs, channels, streams, coupled_streams, mapping);
            if (error != null)
                error.Val = ret;
            if (ret != OpusError.OPUS_OK)
            {
                st = null;
            }
            return st;
        }

        internal delegate void opus_copy_channel_out_func<T>(
          Pointer<T> dst,
          int dst_stride,
          int dst_channel,
          Pointer<short> src,
          int src_stride,
          int frame_size
        );

        internal static int opus_multistream_packet_validate(Pointer<byte> data,
            int len, int nb_streams, int Fs)
        {
            int s;
            int count;
            BoxedValue<byte> toc = new BoxedValue<byte>();
            short[] size = new short[48];
            int samples = 0;
            BoxedValue<int> packet_offset = new BoxedValue<int>();

            for (s = 0; s < nb_streams; s++)
            {
                int tmp_samples;
                if (len <= 0)
                    return OpusError.OPUS_INVALID_PACKET;

                count = OpusPacketInfo.opus_packet_parse_impl(data, len, (s != nb_streams - 1) ? 1 : 0, toc, null,
                                               size.GetPointer(), null, packet_offset);
                if (count < 0)
                    return count;

                tmp_samples = OpusPacketInfo.GetNumSamples(data, packet_offset.Val, Fs);
                if (s != 0 && samples != tmp_samples)
                    return OpusError.OPUS_INVALID_PACKET;
                samples = tmp_samples;
                data = data.Point(packet_offset.Val);
                len -= packet_offset.Val;
            }

            return samples;
        }

        internal int opus_multistream_decode_native<T>(
      Pointer<byte> data,
      int len,
      Pointer<T> pcm,
      opus_copy_channel_out_func<T> copy_channel_out,
      int frame_size,
      int decode_fec,
      int soft_clip
)
        {
            int Fs;
            int s, c;
            int decoder_ptr;
            int do_plc = 0;
            Pointer<short> buf;

            /* Limit frame_size to avoid excessive stack allocations. */
            Fs = this.GetSampleRate();
            frame_size = Inlines.IMIN(frame_size, Fs / 25 * 3);
            buf = Pointer.Malloc<short>(2 * frame_size);
            decoder_ptr = 0;

            if (len == 0)
                do_plc = 1;
            if (len < 0)
            {
                return OpusError.OPUS_BAD_ARG;
            }
            if (do_plc == 0 && len < 2 * this.layout.nb_streams - 1)
            {
                return OpusError.OPUS_INVALID_PACKET;
            }
            if (do_plc == 0)
            {
                int ret = opus_multistream_packet_validate(data, len, this.layout.nb_streams, Fs);
                if (ret < 0)
                {
                    return ret;
                }
                else if (ret > frame_size)
                {
                    return OpusError.OPUS_BUFFER_TOO_SMALL;
                }
            }
            for (s = 0; s < this.layout.nb_streams; s++)
            {
                OpusDecoder dec;
                int ret;

                dec = this.decoders[decoder_ptr++];

                if (do_plc == 0 && len <= 0)
                {
                    return OpusError.OPUS_INTERNAL_ERROR;
                }
                BoxedValue<int> packet_offset = new BoxedValue<int>(0);
                ret = dec.opus_decode_native(
                    data, len, buf, frame_size, decode_fec,
                    (s != this.layout.nb_streams - 1) ? 1 : 0, packet_offset, soft_clip);
                data = data.Point(packet_offset.Val);
                len -= packet_offset.Val;
                if (ret <= 0)
                {
                    return ret;
                }
                frame_size = ret;
                if (s < this.layout.nb_coupled_streams)
                {
                    int chan, prev;
                    prev = -1;
                    /* Copy "left" audio to the channel(s) where it belongs */
                    while ((chan = OpusMultistream.get_left_channel(this.layout, s, prev)) != -1)
                    {
                        copy_channel_out(pcm, this.layout.nb_channels, chan,
                           buf, 2, frame_size);
                        prev = chan;
                    }
                    prev = -1;
                    /* Copy "right" audio to the channel(s) where it belongs */
                    while ((chan = OpusMultistream.get_right_channel(this.layout, s, prev)) != -1)
                    {
                        copy_channel_out(pcm, this.layout.nb_channels, chan,
                           buf.Point(1), 2, frame_size);
                        prev = chan;
                    }
                }
                else {
                    int chan, prev;
                    prev = -1;
                    /* Copy audio to the channel(s) where it belongs */
                    while ((chan = OpusMultistream.get_mono_channel(this.layout, s, prev)) != -1)
                    {
                        copy_channel_out(pcm, this.layout.nb_channels, chan,
                           buf, 1, frame_size);
                        prev = chan;
                    }
                }
            }
            /* Handle muted channels */
            for (c = 0; c < this.layout.nb_channels; c++)
            {
                if (this.layout.mapping[c] == 255)
                {
                    copy_channel_out(pcm, this.layout.nb_channels, c,
                       null, 0, frame_size);
                }
            }

            return frame_size;
        }

        internal static void opus_copy_channel_out_float(
          Pointer<float> dst,
          int dst_stride,
          int dst_channel,
          Pointer<short> src,
          int src_stride,
          int frame_size
        )
        {
            int i;
            if (src != null)
            {
                for (i = 0; i < frame_size; i++)
                    dst[i * dst_stride + dst_channel] = (1 / 32768.0f) * src[i * src_stride];
            }
            else
            {
                for (i = 0; i < frame_size; i++)
                    dst[i * dst_stride + dst_channel] = 0;
            }
        }

        internal static void opus_copy_channel_out_short(
          Pointer<short> dst,
          int dst_stride,
          int dst_channel,
          Pointer<short> src,
          int src_stride,
          int frame_size
        )
        {
            int i;
            if (src != null)
            {
                // fixme: can use arraycopy here for speed
                for (i = 0; i < frame_size; i++)
                    dst[i * dst_stride + dst_channel] = src[i * src_stride];
            }
            else
            {
                for (i = 0; i < frame_size; i++)
                    dst[i * dst_stride + dst_channel] = 0;
            }
        }

        public int DecodeMultistream(
              Pointer<byte> data,
              int len,
              Pointer<short> pcm,
              int frame_size,
              int decode_fec
        )
        {
            return opus_multistream_decode_native<short>(data, len,
                pcm, opus_copy_channel_out_short, frame_size, decode_fec, 0);
        }

        public int DecodeMultistream(Pointer<byte> data,
          int len, Pointer<float> pcm, int frame_size, int decode_fec)
        {
            return opus_multistream_decode_native<float>(data, len,
                pcm, opus_copy_channel_out_float, frame_size, decode_fec, 0);
        }

        #endregion

        #region Getters and setters

        public int GetBandwidth()
        {
            if (decoders == null || decoders.Length == 0)
                return OpusError.OPUS_INVALID_STATE;
            return decoders[0].GetBandwidth();
        }

        public int GetSampleRate()
        {
            if (decoders == null || decoders.Length == 0)
                return OpusError.OPUS_INVALID_STATE;
            return decoders[0].GetSampleRate();
        }

        public int GetGain()
        {
            if (decoders == null || decoders.Length == 0)
                return OpusError.OPUS_INVALID_STATE;
            return decoders[0].GetGain();
        }

        public int GetLastPacketDuration()
        {
            if (decoders == null || decoders.Length == 0)
                return OpusError.OPUS_INVALID_STATE;
            return decoders[0].GetLastPacketDuration();
        }

        public uint GetFinalRange()
        {
            uint value = 0;
            for (int s = 0; s < layout.nb_streams; s++)
            {
                value ^= decoders[s].GetFinalRange();
            }
            return value;
        }

        public void ResetState()
        {
            for (int s = 0; s < layout.nb_streams; s++)
            {
                decoders[s].ResetState();
            }
        }

        public OpusDecoder GetMultistreamDecoderState(int streamId)
        {
            return decoders[streamId];
        }

        public void SetGain(int gain)
        {
            for (int s = 0; s < layout.nb_streams; s++)
            {
                decoders[s].SetGain(gain);
            }
        }

        #endregion
    }
}