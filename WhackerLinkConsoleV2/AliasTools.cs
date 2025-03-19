/*
* WhackerLink - WhackerLinkConsoleV2
*
* This program is free software: you can redistribute it and/or modify
* it under the terms of the GNU General Public License as published by
* the Free Software Foundation, either version 3 of the License, or
* (at your option) any later version.
*
* This program is distributed in the hope that it will be useful,
* but WITHOUT ANY WARRANTY; without even the implied warranty of
* MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
* GNU General Public License for more details.
*
* You should have received a copy of the GNU General Public License
* along with this program.  If not, see <http://www.gnu.org/licenses/>.
* 
* Copyright (C) 2025 Caleb, K4PHP
* 
*/

using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WhackerLinkLib.Models;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.Serialization;
using System.Diagnostics;

namespace WhackerLinkConsoleV2
{
    public static class AliasTools
    {
        public static List<RadioAlias> LoadAliases(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("Alias file not found.", filePath);
            }

            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();

            var yamlText = File.ReadAllText(filePath);
            return deserializer.Deserialize<List<RadioAlias>>(yamlText);
        }

        public static string GetAliasByRid(List<RadioAlias> aliases, int rid)
        {
            if (aliases == null || aliases.Count == 0)
                return string.Empty;

            var match = aliases.FirstOrDefault(a => a.Rid == rid);
            return match?.Alias ?? string.Empty;
        }
    }
}
