﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;
using UnityEngine;
using Harmony;

namespace CombatExtended.Harmony
{
    [HarmonyPatch(typeof(StunHandler), "Notify_DamageApplied")]
    public static class Harmony_StunHandler_Notify_DamageApplied
    {
        public static void Postfix(StunHandler __instance, DamageInfo dinfo, bool affectedByEMP)
        {
            if (dinfo.Def == DamageDefOf.EMP)
            {
                var dmgAmount = dinfo.Amount;
                if (!affectedByEMP) dmgAmount = Mathf.RoundToInt(dmgAmount * 0.25f);
                var newDinfo = new DamageInfo(CE_DamageDefOf.Electrical, dmgAmount, 0, //Armor Penetration 0
                    dinfo.Angle, dinfo.Instigator, dinfo.HitPart, dinfo.Weapon, dinfo.Category);
                __instance.parent.TakeDamage(newDinfo);
            }
        }
    }
}
