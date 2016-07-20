﻿using System;
using System.Collections.Generic;
using System.Text;

namespace NiceHashMiner.Enums
{
    public enum CPUExtensionType : int
    {
        // 0 - automatic, 1 - SSE2, 2 - AVX, 3 - AVX2
        Automatic = 0,
        SSE2,
        AVX,
        AVX2
    }
}