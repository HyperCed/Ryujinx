using NUnit.Framework;
using Ryujinx.HLE.HOS.Services.Am;
using Ryujinx.HLE.HOS.Services.Am.AppletOE.ApplicationProxyService;

namespace Ryujinx.Tests.HLE.Services.Am
{
    class IApplicationProxyServiceTests
    {
        [Test]
        public void Switch2CommandUsesExistingOpenApplicationProxyHandler()
        {
            IApplicationProxyService service = new(null);

            Assert.That(service.CmifCommands[10], Is.SameAs(service.CmifCommands[0]));
        }

        [Test]
        public void Switch2ApplicationProxyGettersAreRegistered()
        {
            IApplicationProxy proxy = new(0);

            Assert.That(proxy.CmifCommands, Does.ContainKey(40));
            Assert.That(proxy.CmifCommands, Does.ContainKey(60));
        }
    }
}
