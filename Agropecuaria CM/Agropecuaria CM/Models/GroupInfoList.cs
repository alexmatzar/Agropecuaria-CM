using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Agropecuaria_CM.Models
{
    internal class GroupInfoList : List<object>
    {
        public GroupInfoList(IEnumerable<object> items) : base(items) { }
        public object Key { get; set; }
        public override string ToString() => $"Group {Key}";
    }
}
