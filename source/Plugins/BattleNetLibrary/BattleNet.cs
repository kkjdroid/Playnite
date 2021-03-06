﻿using Playnite.Common.System;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BattleNetLibrary
{
    public class BattleNet
    {
        public static string ClientExecPath
        {
            get
            {
                var path = InstallationPath;
                return string.IsNullOrEmpty(path) ? string.Empty : Path.Combine(path, "Battle.net.exe");
            }
        }

        public static string InstallationPath
        {
            get
            {
                var progs = Programs.GetUnistallProgramsList().FirstOrDefault(a => a.DisplayName == "Battle.net" || a.DisplayName == "Blizzard App");
                if (progs == null)
                {
                    return string.Empty;
                }
                else
                {
                    return progs.InstallLocation;
                }
            }
        }

        public static bool IsInstalled
        {
            get
            {
                var progs = Programs.GetUnistallProgramsList().FirstOrDefault(a => a.DisplayName == "Battle.net" || a.DisplayName == "Blizzard App");
                if (progs == null)
                {
                    return false;
                }
                else
                {
                    return true;
                }
            }
        }        
    }
}
