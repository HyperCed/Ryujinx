using NUnit.Framework;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.Horizon.Common;

namespace Ryujinx.Tests.HLE.Kernel.Process
{
    class KProcessCapabilitiesTests
    {
        [Test]
        public void AcceptsSwitch2CorePriorityMask()
        {
            int previousCpuCoresCount = KScheduler.CpuCoresCount;
            KScheduler.CpuCoresCount = 6;

            try
            {
                KProcessCapabilities capabilities = new();
                Result result = capabilities.InitializeForUser([0x050073B7], null, true);

                Assert.AreEqual(Result.Success, result);
                Assert.AreEqual(0x3fUL, capabilities.AllowedCpuCoresMask);
            }
            finally
            {
                KScheduler.CpuCoresCount = previousCpuCoresCount;
            }
        }

        [Test]
        public void KeepsSwitch1CorePriorityMaskLimitedToFourCores()
        {
            int previousCpuCoresCount = KScheduler.CpuCoresCount;
            KScheduler.CpuCoresCount = 6;

            try
            {
                KProcessCapabilities capabilities = new();
                Result result = capabilities.InitializeForUser([0x030073B7], null, true);

                Assert.AreEqual(Result.Success, result);
                Assert.AreEqual(0x0fUL, capabilities.AllowedCpuCoresMask);
            }
            finally
            {
                KScheduler.CpuCoresCount = previousCpuCoresCount;
            }
        }

        [Test]
        public void RejectsCoreOutsideSwitch2Range()
        {
            int previousCpuCoresCount = KScheduler.CpuCoresCount;
            KScheduler.CpuCoresCount = 6;

            try
            {
                KProcessCapabilities capabilities = new();
                Result result = capabilities.InitializeForUser([0x060073B7], null, true);

                Assert.AreEqual(KernelResult.InvalidCpuCore, result);
            }
            finally
            {
                KScheduler.CpuCoresCount = previousCpuCoresCount;
            }
        }
    }
}