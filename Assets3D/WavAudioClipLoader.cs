using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace MDSFramework.Assets3D
{
    internal static class WavAudioClipLoader
    {
        private static readonly Dictionary<string, AudioClip?> Cache =
            new Dictionary<string, AudioClip?>(StringComparer.OrdinalIgnoreCase);

        internal static AudioClip? LoadOrGet(string? fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return null;
            }

            string normalized;
            try
            {
                normalized = Path.GetFullPath(fullPath);
            }
            catch
            {
                return null;
            }

            if (Cache.TryGetValue(normalized, out AudioClip? cached))
            {
                if (cached != null)
                {
                    return cached;
                }

                if (ReferenceEquals(cached, null))
                {
                    return null;
                }

                Cache.Remove(normalized);
            }

            AudioClip? clip = LoadFromDisk(normalized);
            Cache[normalized] = clip;

            return clip;
        }

        internal static void ClearCache()
        {
            foreach (KeyValuePair<string, AudioClip?> entry in Cache)
            {
                if (entry.Value != null)
                {
                    UnityEngine.Object.Destroy(entry.Value);
                }
            }

            Cache.Clear();
        }

        private static AudioClip? LoadFromDisk(string fullPath)
        {
            if (!File.Exists(fullPath))
            {
                MdsLog.Warn($"WAV audio file not found: '{fullPath}'");
                return null;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(fullPath);
                if (bytes.Length < 44 ||
                    bytes[0] != (byte)'R' || bytes[1] != (byte)'I' || bytes[2] != (byte)'F' || bytes[3] != (byte)'F' ||
                    bytes[8] != (byte)'W' || bytes[9] != (byte)'A' || bytes[10] != (byte)'V' || bytes[11] != (byte)'E')
                {
                    MdsLog.Warn($"Invalid RIFF/WAVE header in '{fullPath}'.");
                    return null;
                }

                bool hasFmt = false;
                ushort audioFormat = 0;
                ushort channels = 0;
                int sampleRate = 0;
                ushort bitsPerSample = 0;
                int dataOffset = -1;
                int dataLength = 0;

                int offset = 12;
                while (offset + 8 <= bytes.Length)
                {
                    string chunkId = Encoding.ASCII.GetString(bytes, offset, 4);
                    uint rawSize = ReadUInt32LE(bytes, offset + 4);
                    int payloadStart = offset + 8;
                    int remaining = bytes.Length - payloadStart;
                    int safeSize = rawSize > (uint)remaining ? remaining : (int)rawSize;

                    if (chunkId == "fmt " && safeSize >= 16)
                    {
                        audioFormat = ReadUInt16LE(bytes, payloadStart);
                        channels = ReadUInt16LE(bytes, payloadStart + 2);
                        sampleRate = (int)ReadUInt32LE(bytes, payloadStart + 4);
                        bitsPerSample = ReadUInt16LE(bytes, payloadStart + 14);
                        if (audioFormat == 0xFFFE && safeSize >= 40)
                        {
                            // WAVE_FORMAT_EXTENSIBLE: SubFormat GUID starts at +24 (first 2 bytes hold format tag 1 or 3)
                            audioFormat = ReadUInt16LE(bytes, payloadStart + 24);
                        }

                        hasFmt = true;
                    }
                    else if (chunkId == "data" && safeSize > 0)
                    {
                        dataOffset = payloadStart;
                        dataLength = safeSize;
                    }

                    long nextOffset = (long)payloadStart + rawSize + ((rawSize & 1u) != 0 ? 1L : 0L);
                    if (nextOffset <= offset || nextOffset > bytes.Length)
                    {
                        break;
                    }

                    offset = (int)nextOffset;
                }

                if (!hasFmt || dataOffset < 0 || dataLength <= 0 || channels == 0 || channels > 8 || sampleRate <= 0)
                {
                    MdsLog.Warn($"Incomplete or unsupported WAV chunks in '{fullPath}' (fmt={hasFmt}, ch={channels}, sr={sampleRate}, data={dataLength}).");
                    return null;
                }

                int bytesPerSample = bitsPerSample / 8;
                int blockAlign = bytesPerSample * channels;
                if (bytesPerSample <= 0 || blockAlign <= 0)
                {
                    MdsLog.Warn($"Invalid WAV bit depth ({bitsPerSample}-bit) in '{fullPath}'.");
                    return null;
                }

                int sampleFrames = dataLength / blockAlign;
                if (sampleFrames <= 0)
                {
                    return null;
                }

                int totalFloats = sampleFrames * channels;
                var samples = new float[totalFloats];

                if (audioFormat == 1 && bitsPerSample == 16)
                {
                    for (int i = 0; i < totalFloats; i++)
                    {
                        int p = dataOffset + (i * 2);
                        short v = (short)(bytes[p] | (bytes[p + 1] << 8));
                        samples[i] = v / 32768f;
                    }
                }
                else if (audioFormat == 1 && bitsPerSample == 24)
                {
                    for (int i = 0; i < totalFloats; i++)
                    {
                        int p = dataOffset + (i * 3);
                        int v = bytes[p] | (bytes[p + 1] << 8) | (bytes[p + 2] << 16);
                        if ((v & 0x800000) != 0)
                        {
                            v |= unchecked((int)0xFF000000);
                        }

                        samples[i] = v / 8388608f;
                    }
                }
                else if (audioFormat == 1 && bitsPerSample == 32)
                {
                    for (int i = 0; i < totalFloats; i++)
                    {
                        int p = dataOffset + (i * 4);
                        int v = bytes[p] | (bytes[p + 1] << 8) | (bytes[p + 2] << 16) | (bytes[p + 3] << 24);
                        samples[i] = (float)(v / 2147483648.0);
                    }
                }
                else if (audioFormat == 3 && bitsPerSample == 32)
                {
                    for (int i = 0; i < totalFloats; i++)
                    {
                        float f = BitConverter.ToSingle(bytes, dataOffset + (i * 4));
                        if (float.IsNaN(f) || float.IsInfinity(f))
                        {
                            f = 0f;
                        }

                        samples[i] = Mathf.Clamp(f, -1f, 1f);
                    }
                }
                else
                {
                    MdsLog.Warn($"Unsupported WAV encoding format={audioFormat}, bits={bitsPerSample} in '{fullPath}'.");
                    return null;
                }

                string clipName = Path.GetFileNameWithoutExtension(fullPath);
                AudioClip clip = AudioClip.Create(clipName, sampleFrames, channels, sampleRate, stream: false);
                clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
                clip.SetData(samples, 0);
                return clip;
            }
            catch (Exception ex)
            {
                MdsLog.Warn($"Failed loading WAV '{fullPath}': {ex.Message}");
                return null;
            }
        }

        private static ushort ReadUInt16LE(byte[] data, int index)
        {
            return (ushort)(data[index] | (data[index + 1] << 8));
        }

        private static uint ReadUInt32LE(byte[] data, int index)
        {
            return (uint)(data[index] | (data[index + 1] << 8) | (data[index + 2] << 16) | (data[index + 3] << 24));
        }
    }
}
