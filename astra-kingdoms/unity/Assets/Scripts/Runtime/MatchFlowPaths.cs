using System.IO;
using UnityEngine;

namespace AstraKingdoms.Client
{
    /// <summary>Where development builds keep match records and automation evidence.</summary>
    public static class MatchFlowPaths
    {
        public static string RecordsDirectory => Path.Combine(Application.persistentDataPath, "records");
        public static string AutomationDirectory => Path.Combine(Application.persistentDataPath, "automation");
    }
}
