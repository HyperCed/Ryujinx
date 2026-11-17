using Ryujinx.HLE.HOS.Kernel.Common;
using System;

namespace Ryujinx.HLE
{
    public enum MemoryConfiguration
    {
        MemoryConfiguration4GiB = 0,
        MemoryConfiguration6GiB = 1,
        MemoryConfiguration8GiB = 2,
        MemoryConfiguration12GiB = 3,
        MemoryConfiguration4GiBAppletDev = 4,
        MemoryConfiguration4GiBSystemDev = 5,
        MemoryConfiguration6GiBAppletDev = 6,

        // The 12 GiB arrangement with 4 GiB more for the system pool: Switch 2 titles use nearly all of
        // their 11 GiB, and the graphics server backs their GPU-only memory (render targets) from its own.
        MemoryConfiguration16GiB = 7,
    }

    static class MemoryConfigurationExtensions
    {
        private const ulong GiB = 1024 * 1024 * 1024;

        extension(MemoryConfiguration configuration)
        {
            public MemoryArrange KernelMemoryArrange => configuration switch
            {
#pragma warning disable IDE0055 // Disable formatting
                MemoryConfiguration.MemoryConfiguration4GiB          => MemoryArrange.MemoryArrange4GiB,
                MemoryConfiguration.MemoryConfiguration4GiBAppletDev => MemoryArrange.MemoryArrange4GiBAppletDev,
                MemoryConfiguration.MemoryConfiguration4GiBSystemDev => MemoryArrange.MemoryArrange4GiBSystemDev,
                MemoryConfiguration.MemoryConfiguration6GiB          => MemoryArrange.MemoryArrange6GiB,
                MemoryConfiguration.MemoryConfiguration6GiBAppletDev => MemoryArrange.MemoryArrange6GiBAppletDev,
                MemoryConfiguration.MemoryConfiguration8GiB          => MemoryArrange.MemoryArrange8GiB,
                MemoryConfiguration.MemoryConfiguration12GiB         => MemoryArrange.MemoryArrange12GiB,
                MemoryConfiguration.MemoryConfiguration16GiB         => MemoryArrange.MemoryArrange16GiB,
                _ => throw new AggregateException($"Invalid memory configuration \"{configuration}\"."),
#pragma warning restore IDE0055
            };
            
            public MemorySize KernelMemorySize => configuration switch
            {
#pragma warning disable IDE0055 // Disable formatting
                MemoryConfiguration.MemoryConfiguration4GiB or
                    MemoryConfiguration.MemoryConfiguration4GiBAppletDev or
                    MemoryConfiguration.MemoryConfiguration4GiBSystemDev => MemorySize.MemorySize4GiB,
                MemoryConfiguration.MemoryConfiguration6GiB or
                    MemoryConfiguration.MemoryConfiguration6GiBAppletDev => MemorySize.MemorySize6GiB,
                MemoryConfiguration.MemoryConfiguration8GiB              => MemorySize.MemorySize8GiB,
                MemoryConfiguration.MemoryConfiguration12GiB             => MemorySize.MemorySize12GiB,
                MemoryConfiguration.MemoryConfiguration16GiB             => MemorySize.MemorySize16GiB,
                _ => throw new AggregateException($"Invalid memory configuration \"{configuration}\"."),
#pragma warning restore IDE0055
            };
            
            public ulong DramSize => configuration switch
            {
#pragma warning disable IDE0055 // Disable formatting
                MemoryConfiguration.MemoryConfiguration4GiB or
                    MemoryConfiguration.MemoryConfiguration4GiBAppletDev or
                    MemoryConfiguration.MemoryConfiguration4GiBSystemDev => 4 * GiB,
                MemoryConfiguration.MemoryConfiguration6GiB or
                    MemoryConfiguration.MemoryConfiguration6GiBAppletDev => 6 * GiB,
                MemoryConfiguration.MemoryConfiguration8GiB          => 8 * GiB,
                MemoryConfiguration.MemoryConfiguration12GiB         => 12 * GiB,
                MemoryConfiguration.MemoryConfiguration16GiB         => 16 * GiB,
                _ => throw new AggregateException($"Invalid memory configuration \"{configuration}\"."),
#pragma warning restore IDE0055
            };
        }
    }
}
