using ft.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ft.Bandwidth
{
    public class BandwidthTracker
    {
        readonly BlockingCollection<ulong> samples;
        ulong? oldestSample = null;
        public ulong TotalBytesTransferred { get; private set; }

        public BandwidthTracker(int sampleIntervalMs, int reportingIntervalMs)
        {
            var sampleCountToStore = (int)Math.Ceiling(reportingIntervalMs / (double)sampleIntervalMs);

            samples = new BlockingCollection<ulong>(sampleCountToStore);
            SampleIntervalMs = sampleIntervalMs;
            ReportingIntervalMs = reportingIntervalMs;

            Threads.StartNew(TakeSamples, $"{nameof(BandwidthTracker)}.{nameof(TakeSamples)}");
        }

        public int SampleIntervalMs { get; }
        public int ReportingIntervalMs { get; }

        public void SetTotalBytesTransferred(ulong totalBytesTransferred)
        {
            TotalBytesTransferred = totalBytesTransferred;
        }

        public void TakeSamples()
        {
            var maxSampleCount = samples.BoundedCapacity;
            var consumingEnumerable = samples.GetConsumingEnumerable();

            while (true)
            {
                if (samples.Count == maxSampleCount)
                {
                    oldestSample = consumingEnumerable.First();
                }

                samples.Add(TotalBytesTransferred);

                Delay.Wait(SampleIntervalMs);
            }
        }

        public double GetBandwidth_BitsPerSecond()
        {
            if (oldestSample == null) return 0;

            var sampleWindowSeconds = samples.Count * (SampleIntervalMs / 1000d);

            var newestSample = TotalBytesTransferred;

            var bytesTransferred = newestSample - oldestSample.Value;
            var bitsTranferred = bytesTransferred * 8;

            var bandwidthBitsPerSecond = bitsTranferred / sampleWindowSeconds;

            return bandwidthBitsPerSecond;
        }

        public string GetBandwidth()
        {
            var bandwidthBitsPerSecond = GetBandwidth_BitsPerSecond();

            if (!double.IsFinite(bandwidthBitsPerSecond))
            {
                bandwidthBitsPerSecond = 0;
            }

            var ordinals = new[] { "", "K", "M", "G", "T", "P", "E" };
            var ordinal = 0;
            while (bandwidthBitsPerSecond > 1024 && ordinal < ordinals.Length)
            {
                bandwidthBitsPerSecond /= 1024;
                ordinal++;
            }
            var bw = Math.Round(bandwidthBitsPerSecond, 2, MidpointRounding.AwayFromZero);
            var result = $"{bw} {ordinals[ordinal]}b/s";

            return result;
        }
    }
}
