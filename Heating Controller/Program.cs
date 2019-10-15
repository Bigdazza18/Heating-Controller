
using Topshelf;

namespace Heating_Controller
{
    class Program
    {
        static void Main(string[] args)
        {
            HostFactory.Run(host =>
            {
                host.SetServiceName("ClaronHeatingController"); //cannot contain spaces or / or \
                host.SetDisplayName("Heating Controller");
                host.SetDescription("A heating controller that works in conjuction with Domoticz");
                host.StartAutomatically();


                host.Service<HeatingController>();
            });
        }
    }
}
