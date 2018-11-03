﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;
using UnityEngine;

namespace CombatExtended
{
    [DefOf]
    public class CE_DamageDefOf
    {
        public static DamageDef Electrical;

        static CE_DamageDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(CE_DamageDefOf));
        }
    }
}