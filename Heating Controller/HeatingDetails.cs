using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Heating_Controller
{
    class HeatingDetails
    {
        public int ActTime { get; set; }
        public string app_version { get; set; }
        public List<HeatingDetail> result { get; set; }
        public string status { get; set; }
        public string title { get; set; }
    }
}
