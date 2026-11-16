using NUnit.Framework;
using Ryujinx.Graphics.Gpu.Engine.Nx2;
using System.Collections.Generic;

namespace Ryujinx.Tests.Graphics.Nx2
{
    class Nx2QmdSchedulerTests
    {
        private const ulong QmdAddress = 0x6560000000;
        private const ulong ReleaseAddress = 0x6561000000;

        private static uint[] MakeQmd(uint dependencies)
        {
            uint[] qmd = new uint[Nx2QmdScheduler.DescriptorWords];

            qmd[18] = 0x30; // QMD version 3.0
            qmd[4] = dependencies << 14;

            // Release slot 0: enabled, one-word structure, payload 0x42.
            qmd[24] = unchecked((uint)ReleaseAddress);
            qmd[25] = (uint)(ReleaseAddress >> 32) | (1u << 23) | (1u << 30);
            qmd[26] = 0x42;

            return qmd;
        }

        private static (string, List<(ulong, uint)>) Signal(Nx2QmdScheduler scheduler, uint[] qmd, uint argument)
        {
            List<(ulong, uint)> releases = [];

            string error = scheduler.Signal(
                QmdAddress,
                argument,
                (address, descriptor) =>
                {
                    qmd.CopyTo(descriptor, 0);

                    return true;
                },
                (address, payload, flags) =>
                {
                    releases.Add((address, payload));

                    return true;
                },
                descriptor => true);

            return (error, releases);
        }

        [Test]
        public void ScheduledQmdWithoutDependenciesReleasesAtOnce()
        {
            Nx2QmdScheduler scheduler = new();
            (string error, List<(ulong, uint)> releases) = Signal(scheduler, MakeQmd(0), 3);

            Assert.That(error, Is.Null);
            Assert.That(releases, Is.EqualTo(new List<(ulong, uint)> { (ReleaseAddress, 0x42u) }));
        }

        [Test]
        public void QmdWaitsForItsDependencies()
        {
            Nx2QmdScheduler scheduler = new();
            uint[] qmd = MakeQmd(2);

            Assert.That(Signal(scheduler, qmd, 3).Item2, Is.Empty);

            // Decrement by one (action 7, count 1).
            Assert.That(Signal(scheduler, qmd, 7).Item2, Is.Empty);

            (string error, List<(ulong, uint)> releases) = Signal(scheduler, qmd, 7);

            Assert.That(error, Is.Null);
            Assert.That(releases, Has.Count.EqualTo(1));
        }

        [Test]
        public void DecrementBeforeScheduleIsDeferred()
        {
            Nx2QmdScheduler scheduler = new();
            uint[] qmd = MakeQmd(1);

            // Another channel decrements before the QMD is scheduled: the decrement counts once it is.
            Assert.That(Signal(scheduler, qmd, 7).Item2, Is.Empty);
            Assert.That(Signal(scheduler, qmd, 3).Item2, Has.Count.EqualTo(1));
        }

        [TestCase(0u, 5u, 3u, 8u)]
        [TestCase(2u, 5u, 9u, 9u)]
        [TestCase(3u, 5u, 5u, 0u)]
        [TestCase(4u, 5u, 9u, 4u)]
        [TestCase(4u, 5u, 3u, 3u)]
        public void ReducesSemaphores(uint op, uint value, uint payload, uint expected)
        {
            Assert.That(Nx2QmdScheduler.ReduceSemaphore(op, false, value, payload), Is.EqualTo(expected));
        }
    }
}
