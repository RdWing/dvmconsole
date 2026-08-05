// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Desktop Dispatch Console
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @package DVM / Desktop Dispatch Console
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2025 Caleb, K4PHP
*
*/

using System.Reflection;

namespace dvmconsole
{
    /// <summary>
    /// Portable logging seam for the Core-owned audio conversion surface.
    /// Consumers (such as the WPF console) install a <see cref="Route"/> to
    /// receive rendered log lines; with no route installed, logging is a
    /// no-op. This type must stay BCL-only so it can compile into the
    /// headless DvmConsole.Core assembly.
    /// </summary>
    public static class AudioConverterLog
    {
        /// <summary>
        /// Gets or sets a delegate to receive rendered log lines.
        /// </summary>
        public static Action<string> Route { get; set; } = null;

        /// <summary>
        /// Writes a trace message with calling function information, matching
        /// the exact format of the WPF <c>Log.WriteLine(string)</c>: a
        /// <c>&lt;Type::Method(paramTypeNames)&gt; </c> prefix rendered from
        /// the calling frame, followed by the message.
        /// </summary>
        /// <param name="message">Message to print.</param>
        public static void WriteLine(string message)
        {
            string trace = string.Empty;

            MethodBase mb = new System.Diagnostics.StackTrace().GetFrame(1).GetMethod();
            ParameterInfo[] param = mb.GetParameters();
            string funcParams = string.Empty;
            for (int i = 0; i < param.Length; i++)
                if (i < param.Length - 1)
                    funcParams += param[i].ParameterType.Name + ", ";
                else
                    funcParams += param[i].ParameterType.Name;

            trace += "<" + mb.ReflectedType.Name + "::" + mb.Name + "(" + funcParams + ")> ";
            trace += message;

            Route?.Invoke(trace);
        }
    } // public static class AudioConverterLog
} // namespace dvmconsole
