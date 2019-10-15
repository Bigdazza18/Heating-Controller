using NLog;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using System.Xml;
using Topshelf;

namespace Heating_Controller
{
    class HeatingController : ServiceControl
    {
        static string DomoticzIP = ConfigurationManager.AppSettings["DomoticzIP"];
        static string DomoticzPort = ConfigurationManager.AppSettings["DomoticzPort"];
        static string ValveConfigFile = ConfigurationManager.AppSettings["ValveConfigFile"];
        static int CheckInterval = int.Parse(ConfigurationManager.AppSettings["CheckInterval"]);
        static string AllDevicesAPI = ConfigurationManager.AppSettings["AllDevicesAPI"];
        static string BoilerSwitchOnAPI = ConfigurationManager.AppSettings["BoilerSwitchOnAPI"];
        static string BoilerSwitchOffAPI = ConfigurationManager.AppSettings["BoilerSwitchOffAPI"];
        static string TRVhighTempAPI = ConfigurationManager.AppSettings["TRVhighTempAPI"];
        static string TRVlowTempAPI = ConfigurationManager.AppSettings["TRVlowTempAPI"];
        static double OverTemp = double.Parse(ConfigurationManager.AppSettings["OverTemp"]);
        static double UnderTemp = double.Parse(ConfigurationManager.AppSettings["UnderTemp"]);
        static string NotificationAPI = ConfigurationManager.AppSettings["NotificationAPI"];
        static int BoilerTooLongRunTime = int.Parse(ConfigurationManager.AppSettings["BoilerTooLongRunTime"]);

        static Timer CheckTempsTimer = new Timer();
        static RestClient client;
        static Dictionary<string, TrvDetail> AllTrvs = new Dictionary<string, TrvDetail>();

        static Dictionary<string, HeatingDetail> AllDomoticzDevices = new Dictionary<string, HeatingDetail>();

        static string BoilerSwitchIdx;

        static Logger logger = LogManager.GetCurrentClassLogger();

        static Stopwatch BoilerRunTimer = new Stopwatch();

        public bool Start(HostControl hostControl)
        {

            client = new RestClient(string.Format("http://{0}:{1}", DomoticzIP,DomoticzPort));

            //load the trv details
            XmlDocument details = new XmlDocument();
            try
            {
                logger.Info("Loading {0}...", ValveConfigFile);
                details.Load(ValveConfigFile);
                logger.Info("Loaded successfully");
            }
            catch (Exception err)
            {
                logger.Fatal(err, "Unable to load config file");
                return false;
            }

            foreach (XmlNode TRV in details.SelectNodes("/ControllerSettings/TrvDetails/TRV"))
            {
                if (TRV.NodeType != XmlNodeType.Comment)
                    AllTrvs.Add(TRV.Attributes["Name"].InnerText, new TrvDetail() { Name = TRV.Attributes["Name"].InnerText, SetPointIdx = TRV.Attributes["SetPointIdx"].InnerText, TemperatureIdx = TRV.Attributes["TemperatureIdx"].InnerText, ThermostatIdx = TRV.Attributes["ThermostatIdx"].InnerText });
            }

            BoilerSwitchIdx = details.SelectSingleNode("/ControllerSettings/BoilerSwitch").Attributes["Idx"].InnerText;

            CheckTempsTimer.Elapsed += CheckTempsTimer_Elapsed;
            CheckTempsTimer.Interval = CheckInterval * 1000 * 60; 
            CheckTempsTimer.Enabled = true;
            CheckTempsTimer.Start();

            CheckTempsTimer_Elapsed(null, null);

            logger.Info("Timer is running...");

            return true;
        }

        public bool Stop(HostControl hostControl)
        {
            try
            {
                CheckTempsTimer.Stop();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void CheckTempsTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            CheckTempsTimer.Stop();

            AllDomoticzDevices.Clear();

            bool BoilerNeedsToRun = false;
            bool isBoilerOn = false;

            logger.Info("Timer expired! - " + DateTime.Now.ToString("dd/MM/yyyy hh:mm:ss"));
            logger.Info("Query Domoticz for details...");

            //Use API to get all smart devices, this includes light switchs, TRV's and the boiler switch
            var request = new RestRequest(AllDevicesAPI);
            HeatingDetails AllDevices = new HeatingDetails();
            try
            {
                AllDevices = GetData<HeatingDetails>(request);
            }
            catch (Exception err)
            {
                logger.Error("Error querying API. Error: " + err.Message.Trim());
                CheckTempsTimer.Start();
                return;
            }

            foreach (HeatingDetail Detail in AllDevices.result)
            {
                Detail.ID = Detail.ID.TrimStart('0');

                //go through all devices and add them to the correct dictonary 
                if (Detail.Type == "Temp" || (Detail.Type == "Thermostat" && Detail.SubType == "SetPoint"))
                    AllDomoticzDevices.Add(Detail.idx, Detail);
                else if (Detail.Name == "Boiler Switch")
                {
                    isBoilerOn = Detail.Data == "On";
                }
            }

            //Loop through each TRV in the house (TRV also contains the current temperature)
            foreach (KeyValuePair<string, TrvDetail> Entry in AllTrvs)
            {
                if (DoesTRVrequireHeat(Entry.Value, isBoilerOn)) BoilerNeedsToRun = true;
            }

            //All TRV's have now been checked - do any require heat?
            //Now we can check to see if the boiler needs to be off or on
            if (BoilerNeedsToRun)
            {
                //send command to turn on boiler
                //send command even if the boiler was already on as there is no harm
                if(SwitchBoiler(false)) BoilerRunTimer.Start();
            }
            else
            {
               if(SwitchBoiler()) BoilerRunTimer.Reset();
            }


            //check the boiler run time
            if(BoilerRunTimer.Elapsed.Minutes >= BoilerTooLongRunTime)
            {
                //boiler has been running too long, raise an alert
                logger.Info("Boiler has been running too long. Minutes: {0}", BoilerRunTimer.Elapsed.Minutes);
                SendNotification("Boiler has been running too long!!", "The boiler has been running continually for " + BoilerRunTimer.Elapsed.Minutes + " minutes this suggests that there is an issue");
            }


            CheckTempsTimer.Start();
            logger.Info("");
            logger.Info("Timer has been restarted");
        }

        static bool DoesTRVrequireHeat(TrvDetail ConfiguredTRVdetail, bool BoilerIsOn)
        {
            string Name = ConfiguredTRVdetail.Name;
            double CurrentTemp = AllDomoticzDevices[ConfiguredTRVdetail.TemperatureIdx].Temp;
            double TargetTemp = AllDomoticzDevices[ConfiguredTRVdetail.ThermostatIdx].SetPoint;

            double Buffer = BoilerIsOn ? OverTemp : -UnderTemp; //allow a 0.2 degree buffer to stop the boiler flip flopping
            bool BoilerNeedsToRun = CurrentTemp < (TargetTemp + Buffer);

            logger.Debug("---------- TRV: {0} -----------", Name);
            logger.Debug("CurrentTemp: {0}", CurrentTemp.ToString("#.##"));
            logger.Debug("TargetTemp: {0}", TargetTemp.ToString("#.##"));
            logger.Debug("Boiler Was On: {0}", BoilerIsOn);
            logger.Debug("Boiler set to on: {0}", BoilerNeedsToRun);
            logger.Debug("Updated Time: {0}", AllDomoticzDevices[ConfiguredTRVdetail.TemperatureIdx].LastUpdate);

            RestRequest request = new RestRequest("");
            if (BoilerNeedsToRun)
            {

                double test = AllDomoticzDevices[ConfiguredTRVdetail.SetPointIdx].Temp;
                if (AllDomoticzDevices[ConfiguredTRVdetail.SetPointIdx].SetPoint != 28)
                {
                    logger.Info("Setting TRV to 28c...");
                    request = new RestRequest(string.Format(TRVhighTempAPI, ConfiguredTRVdetail.SetPointIdx));

                    var response = client.Execute(request);
                    logger.Info(response.Content);
                }
                else logger.Info("TRV already set to 28c");

            }
            else
            {
                double test = AllDomoticzDevices[ConfiguredTRVdetail.SetPointIdx].Temp;
                if (AllDomoticzDevices[ConfiguredTRVdetail.SetPointIdx].SetPoint != 4)
                {
                    logger.Info("Setting TRV to 4c...");
                    request = new RestRequest(string.Format(TRVlowTempAPI, ConfiguredTRVdetail.SetPointIdx));

                    var response = client.Execute(request);
                    logger.Info(response.Content);
                }
                else logger.Info("TRV already set to 4c");
            }
            logger.Info("--------------------------------------");
            logger.Info("");

            return BoilerNeedsToRun;
        }

        static void SendNotification(string Subject, string Body)
        {
            logger.Info("Sending notification to Domoticz....");
            logger.Debug("Subject: {0}", Subject);
            logger.Debug("Body: {0}", Body);
            var request = new RestRequest(string.Format(NotificationAPI, Subject,Body));
            try
            {
                var response = client.Execute(request);
                logger.Debug(response.Content);
            }
            catch (Exception err)
            {
                logger.Error(err, "Error sending notification");
            }
        }
        
        static bool SwitchBoiler(bool Off = true)
        {
            string State = Off ? "Off" : "On";

            logger.Info("Sending command to switch {0} boiler...", State);
            var request = new RestRequest(string.Format( Off ? BoilerSwitchOffAPI:BoilerSwitchOnAPI, BoilerSwitchIdx));
            try
            {
                var response = client.Execute(request);
                logger.Info(response.Content);
                return response.StatusCode == System.Net.HttpStatusCode.OK;
            }
            catch (Exception err)
            {
                logger.Error(err, "Error sending boiler switch command");
                return false;
            }
        }   

        static T GetData<T>(RestRequest request) where T : new()
        {
            var response = client.Execute<T>(request);

            if (response.ErrorException != null)
            {
                const string message = "Error retrieving response.  Check inner details for more info.";
                var twilioException = new Exception(message, response.ErrorException);
                throw twilioException;
                //Console.WriteLine("Error querying API. Error: {0}", twilioException);
            }
            return response.Data;
        }

    }
}
