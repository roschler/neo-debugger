﻿using System.Collections.Generic;

namespace NeoDebug.Models
{
    public class DebugInfo
    {
        public string Entrypoint { get; set; } = string.Empty;
        public IList<MethodDebugInfo> Methods { get; set; } = new List<MethodDebugInfo>();
        public IList<EventDebugInfo> Events { get; set; } = new List<EventDebugInfo>();
    }
}