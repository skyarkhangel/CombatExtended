﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using RimWorld;
using Verse;
using Verse.AI;
using Verse.Grammar;
using UnityEngine;

namespace CombatExtended
{
    public class Verb_LaunchProjectileCE : Verse.Verb
    {
        #region Constants
        
        // Cover check constants
        private const float distToCheckForCover = 3f;   // How many cells to raycast on the cover check
        private const float segmentLength = 0.2f;       // How long a single raycast segment is
        //private const float shotHeightFactor = 0.85f;   // The height at which pawns hold their guns

        #endregion

        #region Fields

        // Targeting factors
        private float estimatedTargDist = -1;           // Stores estimate target distance for each burst, so each burst shot uses the same
        private int numShotsFired = 0;                  // Stores how many shots were fired for purposes of recoil

        // Angle in Vector2(degrees, radians)
        private Vector2 newTargetLoc = new Vector2(0, 0);
        private Vector2 sourceLoc = new Vector2(0, 0);
        
        private float shotAngle = 0f;   // Shot angle off the ground in radians.
        private float shotRotation = 0f;    // Angle rotation towards target.

        protected CompCharges compCharges = null;
        protected CompAmmoUser compAmmo = null;
        protected CompFireModes compFireModes = null;
        protected CompChangeableProjectile compChangeable = null;
        private float shotSpeed = -1;
        
        private float rotationDegrees = 0f;
        private float angleRadians = 0f;

        private int lastTauntTick;

        private float cachedShotRotation;
        
        #endregion

        #region Properties

        public VerbPropertiesCE VerbPropsCE => this.verbProps as VerbPropertiesCE;
        public ProjectilePropertiesCE projectilePropsCE => this.Projectile.projectile as ProjectilePropertiesCE;

        // Returns either the pawn aiming the weapon or in case of turret guns the turret operator or null if neither exists
        public Pawn ShooterPawn => CasterPawn ?? CE_Utility.TryGetTurretOperator(this.caster);
        public Thing Shooter => ShooterPawn ?? caster;
		
        protected CompCharges CompCharges
        {
            get
            {
                if (this.compCharges == null && this.EquipmentSource != null)
                {
                    this.compCharges = this.EquipmentSource.TryGetComp<CompCharges>();
                }
                return this.compCharges;
            }
        }
        private float ShotSpeed
        {
            get
            {
                if (shotSpeed < 0)
                {
                    if (CompCharges != null)
                    {
                        Vector2 bracket;
                        if (CompCharges.GetChargeBracket((currentTarget.Cell - caster.Position).LengthHorizontal, out bracket))
                        {
                            shotSpeed = bracket.x;
                        }
                    }
                    else
                    {
                    	shotSpeed = Projectile.projectile.speed;
                    }
                }
                return shotSpeed;
            }
        }
        private float ShotHeight => (new CollisionVertical(caster)).shotHeight;
        private Vector3 ShotSource
        {
            get
            {
                var casterPos = caster.DrawPos;
                return new Vector3(casterPos.x, ShotHeight, casterPos.z);
            }
        }

        protected float ShootingAccuracy => Mathf.Min(CasterShootingAccuracyValue(caster), 4.5f);
        protected float AimingAccuracy => Mathf.Min(Shooter.GetStatValue(CE_StatDefOf.AimingAccuracy), 1.5f); //equivalent of ShooterPawn?.GetStatValue(CE_StatDefOf.AimingAccuracy) ?? caster.GetStatValue(CE_StatDefOf.AimingAccuracy)
        protected float SightsEfficiency => EquipmentSource.GetStatValue(CE_StatDefOf.SightsEfficiency);
        protected virtual float SwayAmplitude => Mathf.Max(0, (4.5f - ShootingAccuracy) * EquipmentSource.GetStatValue(StatDef.Named("SwayFactor")));

        // Ammo variables
        protected CompAmmoUser CompAmmo
        {
            get
            {
                if (compAmmo == null && this.EquipmentSource != null)
                {
                    compAmmo = this.EquipmentSource.TryGetComp<CompAmmoUser>();
                }
                return compAmmo;
            }
        }
        public ThingDef Projectile
        {
            get
            {
                if (CompAmmo != null && CompAmmo.CurrentAmmo != null)
                {
                	return CompAmmo.CurAmmoProjectile;
                }
                if (CompChangeable != null && CompChangeable.Loaded)
                {
                	return CompChangeable.Projectile;
                }
                return this.VerbPropsCE.defaultProjectile;
            }
        }
        
        protected CompChangeableProjectile CompChangeable
        {
        	get
        	{
	            if (compChangeable == null && EquipmentSource != null)
	            {
	                compChangeable = EquipmentSource.TryGetComp<CompChangeableProjectile>();
	            }
	            return compChangeable;
        	}
        }
        
        protected CompFireModes CompFireModes
        {
            get
            {
                if (this.compFireModes == null && this.EquipmentSource != null)
                {
                    this.compFireModes = this.EquipmentSource.TryGetComp<CompFireModes>();
                }
                return this.compFireModes;
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Gets caster's weapon handling based on if it's a pawn or a turret
        /// </summary>
        /// <param name="caster">What thing is equipping the projectile launcher</param>
        private float CasterShootingAccuracyValue(Thing caster) => // ShootingAccuracy was split into ShootingAccuracyPawn and ShootingAccuracyTurret
            (caster as Pawn != null) ? caster.GetStatValue(StatDefOf.ShootingAccuracyPawn) : caster.GetStatValue(StatDefOf.ShootingAccuracyTurret);

        /// <summary>
        /// Resets current burst shot count and estimated distance at beginning of the burst
        /// </summary>
        public override void WarmupComplete()
        {
            // attack shooting expression
            if ((ShooterPawn?.Spawned ?? false) && currentTarget.Thing is Pawn && Rand.Chance(0.25f))
            {
                var tauntThrower = (TauntThrower)ShooterPawn.Map.GetComponent(typeof(TauntThrower));
                tauntThrower?.TryThrowTaunt(CE_RulePackDefOf.AttackMote, ShooterPawn);
            }

            this.numShotsFired = 0;
            base.WarmupComplete();
            Find.BattleLog.Add(
            	new BattleLogEntry_RangedFire(
            		Shooter,
            		(!currentTarget.HasThing) ? null : currentTarget.Thing,
            		(EquipmentSource == null) ? null : EquipmentSource.def,
            		Projectile,
            		VerbPropsCE.burstShotCount > 1)
            );
        }
        
        /// <summary>
        /// Shifts the original target position in accordance with target leading, range estimation and weather/lighting effects
        /// </summary>
        protected virtual void ShiftTarget(ShiftVecReport report, bool calculateMechanicalOnly = false)
        {
        	if (!calculateMechanicalOnly)
        	{
	        	Vector3 u = CasterPawn != null ? CasterPawn.DrawPos : caster.Position.ToVector3Shifted();
	        	sourceLoc.Set(u.x, u.z);

            //    Log.Message("u caster pos: " + u.ToIntVec3().ToString() + "u.x: " + u.x.ToString() + "u.z: " + u.z.ToString());


        		if (this.numShotsFired == 0)
        		{
	            	// On first shot of burst do a range estimate
        			estimatedTargDist = report.GetRandDist();
            //        Log.Message("estimatedTargDist: " + estimatedTargDist.ToString());
        		}
	            Vector3 v = report.targetPawn != null ? report.targetPawn.DrawPos + report.targetPawn.Drawer.leaner.LeanOffset * 0.5f : report.target.Cell.ToVector3Shifted();
	            newTargetLoc.Set(v.x, v.z);

            //    if (report.targetPawn != null)
            //    {
            //        Log.Message("targetPawn: " + report.targetPawn.ToString() + " pos: " + report.targetPawn.Position.ToString() + " leanoffset: " + report.targetPawn.DrawPos + (report.targetPawn.Drawer.leaner.LeanOffset * 0.5f).ToString());
            //    }

            //    Log.Message("report.target.Cell.ToVector3Shifted(): " + report.target.Cell.ToVector3Shifted().ToString() + " report.target.Cell: " + report.target.Cell.ToString());

            //    Log.Message("newtargetLoc 1: " + newTargetLoc.ToString());

                // ----------------------------------- STEP 1: Actual location + Shift for visibility

                //FIXME : GetRandCircularVec may be causing recoil to be unnoticeable - each next shot in the burst has a new random circular vector around the target.
                newTargetLoc += report.GetRandCircularVec();

            //    Log.Message("newtargetLoc 2: " + newTargetLoc.ToString() + " report.GetRandCircularVec(): " +  report.GetRandCircularVec().ToString());
                // ----------------------------------- STEP 2: Estimated shot to hit location

                newTargetLoc = sourceLoc + (newTargetLoc - sourceLoc).normalized * estimatedTargDist;

            //    Log.Message("newtargetLoc 3: " + newTargetLoc.ToString() + " sourceLoc: " + sourceLoc.ToString() + " estimatedTargDist" + estimatedTargDist.ToString() + " (newTargetLoc - sourceLoc).normalized * estimatedTargDist: " + ((newTargetLoc - sourceLoc).normalized * estimatedTargDist).ToString());
                // Lead a moving target
                newTargetLoc += report.GetRandLeadVec();

            //    Log.Message("newtargetLoc 4 : " + newTargetLoc.ToString() + " report.GetRandLeadVec(): " + report.GetRandLeadVec().ToString());

                // ----------------------------------- STEP 3: Recoil, Skewing, Skill checks, Cover calculations

                rotationDegrees = 0f;
	            angleRadians = 0f;
	            
	            GetSwayVec(ref rotationDegrees, ref angleRadians);
	            GetRecoilVec(ref rotationDegrees, ref angleRadians);
	
			    // Height difference calculations for ShotAngle
			    float targetHeight = 0f;
	            
	            var coverRange = new CollisionVertical(report.cover).HeightRange;   //Get " " cover, assume it is the edifice

            //   Log.Message("coverRange: " + coverRange.ToString() + " max: " + coverRange.max.ToString() + " min: " + coverRange.min.ToString());

	            // Projectiles with flyOverhead target the surface in front of the target
	            if (Projectile.projectile.flyOverhead)
	            {
	            	targetHeight = coverRange.max;
            //        Log.Message("targetHeight 1: " + targetHeight.ToString());
                }
	            else
	            {
                    var victimVert = new CollisionVertical(currentTarget.Thing);
            //        Log.Message("victimVert : " + victimVert.ToString() + " currentTarget.Thing: " + currentTarget.Thing.ToString() + " pos: " + currentTarget.Thing.Position.ToString());

                    var targetRange = victimVert.HeightRange;	//Get lower and upper heights of the target

            //        Log.Message("targetRange : " + targetRange.ToString() + " max: " + targetRange.max.ToString() + " min: " + targetRange.min.ToString());
                    /*if (currentTarget.Thing is Building && CompFireModes?.CurrentAimMode == AimMode.SuppressFire)
                    {
                    	targetRange.min = targetRange.max;
                    	targetRange.max = targetRange.min + 1f;
                    }*/
                    if (targetRange.min < coverRange.max)	//Some part of the target is hidden behind some cover
	           		{
	           			// - It is possible for targetRange.max < coverRange.max, technically, in which case the shooter will never hit until the cover is gone.
                        // - This should be checked for in LoS -NIA
	           			targetRange.min = coverRange.max;

                        // Target fully hidden, shift aim upwards if we're doing suppressive fire
                        if (targetRange.max <= coverRange.max && CompFireModes?.CurrentAimMode == AimMode.SuppressFire)
                        {
                            targetRange.max = coverRange.max * 2;
                        }
	           		}
                    else if (currentTarget.Thing is Pawn)
                    {
                        // Aim for center of mass on an exposed target
                        targetRange.min = victimVert.BottomHeight;
                        targetRange.max = victimVert.MiddleHeight;
        //                Log.Message("targetHeight 2: " + targetHeight.ToString());
                    }
	           		targetHeight = targetRange.Average;
        //            Log.Message("targetHeight 3: " + targetHeight.ToString());
                }
	            angleRadians += ProjectileCE.GetShotAngle(ShotSpeed, (newTargetLoc - sourceLoc).magnitude, targetHeight - ShotHeight, Projectile.projectile.flyOverhead, projectilePropsCE.Gravity);

        //        Log.Message("angleRadians: " + angleRadians.ToString());
            }
        	
	        // ----------------------------------- STEP 4: Mechanical variation
	        
            // Get shotvariation, in angle Vector2 RADIANS.
            Vector2 spreadVec = report.GetRandSpreadVec();

        //    Log.Message("spreadVec: " + spreadVec.ToString());

            // ----------------------------------- STEP 5: Finalization

            var w = (newTargetLoc - sourceLoc);
            shotRotation = (-90 + Mathf.Rad2Deg * Mathf.Atan2(w.y, w.x) + rotationDegrees + spreadVec.x) % 360;
            shotAngle = angleRadians + spreadVec.y * Mathf.Deg2Rad;

        //    Log.Message("Mathf.Atan2(-w.y, w.x): " + Mathf.Atan2(-w.y, w.x).ToString() + "rotationDegrees: " + rotationDegrees.ToString() + "spreadVec.x: " + spreadVec.x.ToString());
        //    Log.Message("w: " + w.ToString() + "shotRotation: " +shotRotation.ToString() + " shotAngle" + shotAngle.ToString());


        }

        /// <summary>
        /// Calculates the amount of recoil at a given point in a burst, up to a maximum
        /// </summary>
        /// <param name="rotation">The ref float to have horizontal recoil in degrees added to.</param>
        /// <param name="angle">The ref float to have vertical recoil in radians added to.</param>
        private void GetRecoilVec(ref float rotation, ref float angle)
        {
            var recoil = VerbPropsCE.recoilAmount;
            float maxX = recoil * 0.5f;
            float minX = -maxX;
            float maxY = recoil;
            float minY = -recoil / 3;
            /*
            switch (VerbPropsCE.recoilPattern)
            {
                case RecoilPattern.None:
            		return;
                case RecoilPattern.Regular:
                    float num = VerbPropsCE.recoilAmount / 3;
                    minX = -(num / 3);
                    maxX = num;
                    minY = -num;
                    maxY = VerbPropsCE.recoilAmount;
                    break;
                case RecoilPattern.Mounted:
                    float num2 = VerbPropsCE.recoilAmount / 3;
                    minX = -num2;
                    maxX = num2;
                    minY = -num2;
                    maxY = VerbPropsCE.recoilAmount;
                    break;
            }
            */
            float recoilMagnitude = Mathf.Pow((5 - ShootingAccuracy), (Mathf.Min(10, numShotsFired) / 6.25f));

        //    Log.Message("ShootingAccuracy: " + ShootingAccuracy.ToString() + " AimingAccuracy: " + AimingAccuracy.ToString() + " SightsEfficiency: " + SightsEfficiency.ToString());


            rotation += recoilMagnitude * UnityEngine.Random.Range(minX, maxX);
            angle += Mathf.Deg2Rad * recoilMagnitude * UnityEngine.Random.Range(minY, maxY);
        }

        /// <summary>
        /// Calculates current weapon sway based on a parametric function with maximum amplitude depending on shootingAccuracy and scaled by weapon's swayFactor.
        /// </summary>
        /// <param name="rotation">The ref float to have horizontal sway in degrees added to.</param>
        /// <param name="angle">The ref float to have vertical sway in radians added to.</param>
        protected void GetSwayVec(ref float rotation, ref float angle)
        {
        	float ticks = (float)(Find.TickManager.TicksAbs + Shooter.thingIDNumber);
        	rotation += SwayAmplitude * (float)Mathf.Sin(ticks * 0.022f);
        	angle += Mathf.Deg2Rad * 0.25f * SwayAmplitude * (float)Mathf.Sin(ticks * 0.0165f);
        }

        public virtual ShiftVecReport ShiftVecReportFor(LocalTargetInfo target)
        {
            IntVec3 targetCell = target.Cell;
            ShiftVecReport report = new ShiftVecReport();
            report.target = target;
            report.aimingAccuracy = this.AimingAccuracy;
            report.sightsEfficiency = this.SightsEfficiency;
            report.shotDist = (targetCell - this.caster.Position).LengthHorizontal;
            report.maxRange = verbProps.range;

            report.lightingShift = 1 - caster.Map.glowGrid.GameGlowAt(targetCell);
            if (!this.caster.Position.Roofed(caster.Map) || !targetCell.Roofed(caster.Map))  //Change to more accurate algorithm?
            {
                report.weatherShift = 1 - caster.Map.weatherManager.CurWeatherAccuracyMultiplier;
            }
            report.shotSpeed = this.ShotSpeed;
            report.swayDegrees = this.SwayAmplitude;
            var spreadmult = this.projectilePropsCE != null ? this.projectilePropsCE.spreadMult : 0f;
            report.spreadDegrees = this.EquipmentSource.GetStatValue(StatDef.Named("ShotSpread")) * spreadmult;
            Thing cover;
            float smokeDensity;
            this.GetHighestCoverAndSmokeForTarget(target, out cover, out smokeDensity);
            report.cover = cover;
            report.smokeDensity = smokeDensity;

            return report;
        }

        /// <summary>
        /// Checks for cover along the flight path of the bullet, doesn't check for walls or trees, only intended for cover with partial fillPercent
        /// </summary>
        /// <param name="target">The target of which to find cover of</param>
        /// <param name="cover">Output parameter, filled with the highest cover object found</param>
        /// <returns>True if cover was found, false otherwise</returns>
        private bool GetHighestCoverAndSmokeForTarget(LocalTargetInfo target, out Thing cover, out float smokeDensity)
        {
            Map map = caster.Map;
            Thing targetThing = target.Thing;
            Thing highestCover = null;
            float highestCoverHeight = 0f;

            smokeDensity = 0;

            // Iterate through all cells on line of sight and check for cover and smoke
            var cells = GenSight.PointsOnLineOfSight(target.Cell, caster.Position).ToArray();
            if (cells.Length < 3)
            {
                cover = null;
                return false;
            }
            for (int i = 0; i <= cells.Length / 2; i++)
            {
                var cell = cells[i];

                if (cell.AdjacentTo8Way(caster.Position)) continue;

                // Check for smoke
                var gas = cell.GetGas(map);
                if (gas != null)
                {
                    smokeDensity += gas.def.gas.accuracyPenalty;
                }

                // Check for cover in the second half of LoS
                if (i <= cells.Length / 2)
                {
                    Pawn pawn = cell.GetFirstPawn(map);
                    Thing newCover = pawn == null ? cell.GetCover(map) : pawn;
                    float newCoverHeight = new CollisionVertical(newCover).Max;

                    // Cover check, if cell has cover compare collision height and get the highest piece of cover, ignore if cover is the target (e.g. solar panels, crashed ship, etc)
                    if (newCover != null
                        && (targetThing == null || !newCover.Equals(targetThing))
                        && (highestCover == null || highestCoverHeight < newCoverHeight)
                        && newCover.def.Fillage == FillCategory.Partial
                        && !newCover.IsPlant())
                    {
                        highestCover = newCover;
                        highestCoverHeight = newCoverHeight;
                        if (Controller.settings.DebugDrawTargetCoverChecks) map.debugDrawer.FlashCell(cell, highestCoverHeight, highestCoverHeight.ToString());
                    }
                }
            }
            cover = highestCover;

            //Report success if found cover
            return cover != null;
        }

        /// <summary>
        /// Checks if the shooter can hit the target from a certain position with regards to cover height
        /// </summary>
        /// <param name="root">The position from which to check</param>
        /// <param name="targ">The target to check for line of sight</param>
        /// <returns>True if shooter can hit target from root position, false otherwise</returns>
        public override bool CanHitTargetFrom(IntVec3 root, LocalTargetInfo targ)
        {
            string unused;
            return CanHitTargetFrom(root, targ, out unused);
        }

        public bool CanHitTarget(LocalTargetInfo targ, out string report)
        {
            return CanHitTargetFrom(caster.Position, targ, out report);
        }

        public virtual bool CanHitTargetFrom(IntVec3 root, LocalTargetInfo targ, out string report)
        {
            report = "";
            if (!targ.Cell.InBounds(caster.Map) || !root.InBounds(caster.Map))
            {
                report = "CE_OutOfBounds".Translate();
                return false;
            }
            // Check target self
            if (targ.Thing != null && targ.Thing == this.caster)
            {
                if (!verbProps.targetParams.canTargetSelf)
                {
                    report = "CE_CantTargetSelf".Translate();
                    return false;
                }
                return true;
            }
            // Check thick roofs
            if (Projectile.projectile.flyOverhead)
            {
                RoofDef roofDef = caster.Map.roofGrid.RoofAt(targ.Cell);
                if (roofDef != null && roofDef.isThickRoof)
                {
                    report = "CE_BlockedByRoof".Translate();
                    return false;
                }
            }
            if (ShooterPawn != null)
            {
                // Check for capable of violence
                if (ShooterPawn.story != null && ShooterPawn.story.WorkTagIsDisabled(WorkTags.Violent))
                {
                    report = "IsIncapableOfViolenceLower".Translate(ShooterPawn.Name.ToStringShort, ShooterPawn);
            		return false;
        	    }
            	
           		// Check for apparel
            	if (ShooterPawn.apparel != null)
            	{
	                List<Apparel> wornApparel = ShooterPawn.apparel.WornApparel;
	                foreach(Apparel current in wornApparel)
	                {
                        if (!current.AllowVerbCast(root, caster.Map, targ, this))
                        {
	                        report = "CE_ShootingDisallowedBy".Translate() + current.LabelShort;
	                        return false;
	                    }
	                }
            	}
            }
            // Check for line of sight
            ShootLine shootLine;
            if (!TryFindCEShootLineFromTo(root, targ, out shootLine))
            {
                float lengthHorizontalSquared = (root - targ.Cell).LengthHorizontalSquared;
                if (lengthHorizontalSquared > verbProps.range * verbProps.range)
                {
                    report = "CE_OutOfRange".Translate();
                }
                else if(lengthHorizontalSquared < verbProps.minRange * verbProps.minRange)
                {
                    report = "CE_WithinMinimumRange".Translate();
                }
                else
                {
                    report = "CE_NoLineOfSight".Translate();
                }
                return false;
            }
            return true;
        }

        /// <summary>
        /// Fires a projectile using the new aiming system
        /// </summary>
        /// <returns>True for successful shot, false otherwise</returns>
        protected override bool TryCastShot()
        {
            ShootLine shootLine;
            if (!TryFindCEShootLineFromTo(caster.Position, currentTarget, out shootLine))
            {
                return false;
            }

            Pawn p = CasterPawn != null ? CasterPawn : caster as Pawn;


            if (caster != null)
            {
        //        Log.Message("caster: " + caster.ToString() + " pos: " + caster.Position.ToString());
            }
            if (CasterPawn != null)
            {
        //        Log.Message("casterPawn: " + CasterPawn.ToString() + " pos: " + CasterPawn.Position.ToString());
            }
        //    Log.Message("currentTarget: " + currentTarget.ToString() + " pos: " + currentTarget.Cell.ToString());



            if (p != null && !p.IsColonistPlayerControlled && p.equipment != null && p.equipment.Primary != null)
            {
                if (numShotsFired == 0)
                {
                    CompFireModes compFireModes = p.equipment.Primary.TryGetComp<CompFireModes>();
                    if (compFireModes != null)
                    {
                        float lengthToTarget = Mathf.Abs((currentTarget.Cell - caster.Position).LengthHorizontal);
                        float rangemultiplier = this.VerbPropsCE.range / 60;

                        if (lengthToTarget <= (30 * rangemultiplier))
                        {
                            if (compFireModes.availableAimModes.Contains(AimMode.Snapshot) && compFireModes.currentAimModeInt != AimMode.Snapshot)
                            {
                                compFireModes.currentAimModeInt = AimMode.Snapshot;
                            //    Log.Message("selected aimmode1: " + compFireModes.currentAimModeInt);
                            }
                        }
                        else
                        {
                            if (compFireModes.availableAimModes.Contains(AimMode.AimedShot) && compFireModes.currentAimModeInt != AimMode.AimedShot)
                            {
                                compFireModes.currentAimModeInt = AimMode.AimedShot;
                            //    Log.Message("selected aimmode2: " + compFireModes.currentAimModeInt);
                            }
                        }
                        if (compFireModes.availableFireModes.Count > 1)
                        {
                            if (lengthToTarget <= (50 * rangemultiplier))
                            {
                                if (compFireModes.availableFireModes.Contains(FireMode.AutoFire) && compFireModes.currentFireModeInt != FireMode.AutoFire)
                                {
                                    compFireModes.currentFireModeInt = FireMode.AutoFire;
                                //    Log.Message("selected firemode1: " + compFireModes.currentFireModeInt.ToString());
                                }
                            }
                            else
                            {
                                if (compFireModes.availableFireModes.Contains(FireMode.SingleFire) && compFireModes.currentFireModeInt != FireMode.SingleFire)
                                {
                                    compFireModes.currentFireModeInt = FireMode.SingleFire;
                                //    Log.Message("selected firemode2: " + compFireModes.currentFireModeInt.ToString());
                                }
                            }
                        }
                    }
                }
            }

            if (p != null)
            {
                if ((Mathf.Abs(cachedShotRotation - shotRotation)) >= 15 && numShotsFired == 0)
                {
                    cachedShotRotation = shotRotation;
                    p.Drawer.Notify_WarmingCastAlongLine(shootLine, caster.Position);
                }
            }


            if (projectilePropsCE.pelletCount < 1)
            {
                Log.Error(EquipmentSource.LabelCap + " tried firing with pelletCount less than 1.");
                return false;
            }
            ShiftVecReport report = ShiftVecReportFor(currentTarget);
           	bool pelletMechanicsOnly = false;
            for (int i = 0; i < projectilePropsCE.pelletCount; i++)
            {
                ProjectileCE projectile = (ProjectileCE)ThingMaker.MakeThing(Projectile, null);
                GenSpawn.Spawn(projectile, shootLine.Source, caster.Map);
	           	ShiftTarget(report, pelletMechanicsOnly);

                //New aiming algorithm
                projectile.canTargetSelf = false;
                projectile.minCollisionSqr = (sourceLoc - currentTarget.Cell.ToIntVec2.ToVector2Shifted()).sqrMagnitude;
                projectile.intendedTarget = currentTarget.Thing;
                projectile.Launch(
                	Shooter,	//Shooter instead of caster to give turret operators' records the damage/kills obtained
                	sourceLoc,
                	shotAngle,
                	shotRotation,
                	ShotHeight,
                	ShotSpeed,
                	EquipmentSource
                );
	           	pelletMechanicsOnly = true;
            //    Log.Message("proj: " + projectile.ToString() + " shootlineSource: " + shootLine.Source.ToString() + " caster: " + caster.ToString() + " currentTarget.Thing: " + currentTarget.Thing.ToString() + " report: " + report.ToString());
            //    Log.Message("minCollisionSqr: " + projectile.minCollisionSqr.ToString() + " intendedTarget : " + projectile.intendedTarget.ToString() + " Shooter: " + Shooter.ToString()
            //        + " sourceLoc: " + sourceLoc.ToString() + " shotAngle: " + shotAngle.ToString() + " shotRotation: " + shotRotation.ToString() + " ShotHeight: " + ShotHeight.ToString()
            //        + " ShotHeight: " + ShotHeight.ToString() + "ShotSpeed: " + ShotSpeed.ToString() + " EquipmentSource: " + EquipmentSource.ToString());
            }
           	pelletMechanicsOnly = false;
            this.numShotsFired++;
            return true;
        }

        /// <summary>
        /// This is a custom CE ticker. Since the vanilla VerbTick() method is non-virtual we need to detour VerbTracker and make it call this method in addition to the vanilla ticker in order to
        /// add custom ticker functionality.
        /// </summary>
        public virtual void VerbTickCE()
        {
        }

        #region Line of Sight Utility

        /* Line of sight calculating methods
         * 
         * Copied from vanilla Verse.Verb class, the only change here is usage of our own validator for partial cover checks. Copy-paste should be kept up to date with vanilla
         * and if possible replaced with a cleaner solution.
         * 
         * -NIA
         */

        private static List<IntVec3> tempDestList = new List<IntVec3>();
        private static List<IntVec3> tempLeanShootSources = new List<IntVec3>();
        private IntVec3 bestshootposition = new IntVec3();

        /*
        public bool CanSeeTarget(LocalTargetInfo targ)
        {
            var glow = targ.Thing.Map.glowGrid.GameGlowAt(targ.Cell);
            var glowmultiplier = Mathf.Lerp(1, 5, Mathf.Clamp(glow * 5f, 0f, 1f));

            float lengthToTarget = Mathf.Abs((targ.Cell - caster.Position).LengthHorizontal);
            if (targ.Thing.Map.glowGrid.GameGlowAt(targ.Cell) <= 0.2f)
            {
                Log.Message("target: " + targ.Thing +  " glow: " + glow.ToString() + " glowmult: " + glowmultiplier.ToString());
                if (lengthToTarget > glowmultiplier)
                {
                    Log.Message("cant see: " + targ.Thing.ToString());
                    return false;
                }
            }
            return true;
        }
        */

        public bool TryFindCEShootLineFromTo(IntVec3 root, LocalTargetInfo targ, out ShootLine resultingLine)
        {
        //    Log.Message("root: " + root.ToString() + " target: " + targ.Thing.ToString() + " pos: " + targ.Cell.ToString());
            if (targ.HasThing && targ.Thing.Map != this.caster.Map)
            {
                resultingLine = default(ShootLine);
        //        Log.Message("shootline false: 1" + " line: " + resultingLine.ToString());
                return false;
            }
        //    Log.Message("effminrange: " + this.verbProps.EffectiveMinRange(targ, this.caster).ToString() + " meleerange: " + ShootTuning.MeleeRange);

            if (this.verbProps.IsMeleeAttack)
            {
                resultingLine = new ShootLine(root, targ.Cell);
                var ri = ReachabilityImmediate.CanReachImmediate(root, targ, this.caster.Map, PathEndMode.Touch, null);
                //        Log.Message("ri: " + ri.ToString() + " line: " + resultingLine.ToString());
                return ri;
            }

            CellRect cellRect = (!targ.HasThing) ? CellRect.SingleCell(targ.Cell) : targ.Thing.OccupiedRect();
            float num = cellRect.ClosestDistSquaredTo(root);
            if (num > this.verbProps.range * this.verbProps.range || num < this.verbProps.minRange * this.verbProps.minRange)
            {
                resultingLine = new ShootLine(root, targ.Cell);
        //        Log.Message("shootline false: 2" + " line: " + resultingLine.ToString());
                return false;
            }

            //if (!this.verbProps.NeedsLineOfSight) This method doesn't consider the currently loaded projectile
            if (Projectile.projectile.flyOverhead)
            {
                resultingLine = new ShootLine(root, targ.Cell);
        //        Log.Message("shootline true: 3" + " line: " + resultingLine.ToString());
                return true;
            }

            if (this.CasterIsPawn)
            {
                IntVec3 dest;
                ShootLeanUtility.LeanShootingSourcesFromTo(root, cellRect.ClosestCellTo(root), this.caster.Map, tempLeanShootSources);

                if (tempLeanShootSources.Count < 2)
                {
                    if (this.CanHitFromCellIgnoringRange(root, root, targ, out dest))
                    {
                        resultingLine = new ShootLine(root, dest);
        //              Log.Message("shootline true: 4" + " line: " + resultingLine.ToString());
                        return true;
                    }
                }

                if (tempLeanShootSources.Count >= 2)
                {
                    int bestZ = 0;
                    int bestX = 0;
                    int cachedbz = -1;
                    int cachedbx = -1;

                //    Log.Message("pawn: " + CasterPawn.ToString() + " pawn pos: " + root.ToString() + " target: " + targ.Thing.ToString() + " target pos: " + targ.Cell.ToString());
                    foreach (var bsp in tempLeanShootSources)
                    {
                        var bz = Mathf.Abs(bsp.z - targ.Cell.z);
                        if (cachedbz < 0 || (bz < cachedbz))
                        {
                            cachedbz = bz;
                            bestZ = bsp.z;
                        }

                        var bx = Mathf.Abs(bsp.x - targ.Cell.x);
                        if (cachedbx < 0 || (bx < cachedbx))
                        {
                            cachedbx = bx;
                            bestX = bsp.x;
                        }
                    }
                    bestshootposition = new IntVec3(bestX, root.y, bestZ);
                //    Log.Message("bestshootposition: " + bestshootposition.ToString());

                    if (this.CanHitFromCellIgnoringRange(bestshootposition, root, targ, out dest))
                    {
                        resultingLine = new ShootLine(bestshootposition, dest);
                //        Log.Message("shootline true: 5" + " line: " + resultingLine.ToString());
                        return true;
                    }
                }
                else
                {
                    for (int i = 0; i < tempLeanShootSources.Count; i++)
                    {
                        IntVec3 intVec = tempLeanShootSources[i];
                        if (this.CanHitFromCellIgnoringRange(intVec, root, targ, out dest))
                        {
                            resultingLine = new ShootLine(intVec, dest);
                //            Log.Message("shootline true: 6" + " line: " + resultingLine.ToString());
                            return true;
                        }
                    }
                }
            }
            else
            {
                CellRect.CellRectIterator iterator = this.caster.OccupiedRect().GetIterator();
                while (!iterator.Done())
                {
                    IntVec3 current = iterator.Current;
                    IntVec3 dest;
                    if (this.CanHitFromCellIgnoringRange(current, root, targ, out dest))
                    {
                        resultingLine = new ShootLine(current, dest);
            //            Log.Message("shootline true: 7: " + " line: " + resultingLine.ToString());
                        return true;
                    }
                    iterator.MoveNext();
                }
            }
            resultingLine = new ShootLine(root, targ.Cell);
        //    Log.Message("shootline false: 8" + " line: " + resultingLine.ToString());
            return false;
        }

        private bool CanHitFromCellIgnoringRange(IntVec3 sourceCell, IntVec3 rootCell, LocalTargetInfo targ, out IntVec3 goodDest)
        {
            if (targ.Thing != null)
            {
                if (targ.Thing.Map != this.caster.Map)
                {
                    goodDest = IntVec3.Invalid;
                    return false;
                }
                // (ProfoundDarkness) I only ever see this code execute for the target, not the shooter...
                // (ProfoundDarkness) I don't know what I'm doing here so basically if the target has a structure next to them assume they will lean, rather than a more precise and accurate
                // test.

                // If the target is near something they might have to lean around to shoot then calculate their leans.
                // NOTE: CellsAdjacent8Way includes the check for if a location is in map bounds so can use CanBeSeenOverFast.  The alternative is fast 8way and slow (bounds checking) CanBeSeenOver.
                /*
                if ((targ.Thing as Pawn)?.CurJob?.def != CE_JobDefOf.HunkerDown && GenAdjFast.AdjacentCells8Way(targ.Thing).FirstOrDefault(c => !c.CanBeSeenOver(targ.Thing.Map)) != null)
                {
                    ShootLeanUtility.CalcShootableCellsOf(tempDestList, targ.Thing);
                } else // otherwise just assume that the target won't lean...
                {
                    tempDestList.Clear();
                    tempDestList.Add(targ.Cell);
                }
                */
                tempDestList.Clear();
                tempDestList.Add(targ.Cell);

                for (int i = 0; i < tempDestList.Count; i++)
                {
                    if (this.CanHitCellFromCellIgnoringRange(sourceCell, rootCell, tempDestList[i], targ.Thing, targ.Thing.def.Fillage == FillCategory.Full))
                    {   // if any of the locations the target is at or can lean to for shooting can be shot by the shooter then lets have the shooter shoot.
                        goodDest = tempDestList[i];
                        return true;
                    }
                }
            }
            else if (this.CanHitCellFromCellIgnoringRange(sourceCell, rootCell, targ.Cell, targ.Thing))
            {
                goodDest = targ.Cell;
                return true;
            }
            goodDest = IntVec3.Invalid;
            return false;
        }

        // Added targetThing to parameters so we can calculate its height
        private bool CanHitCellFromCellIgnoringRange(IntVec3 sourceSq, IntVec3 rootCell, IntVec3 targetLoc, Thing targetThing = null, bool includeCorners = false)
        {
            // Vanilla checks
            if (this.verbProps.mustCastOnOpenGround && (!targetLoc.Standable(this.caster.Map) || this.caster.Map.thingGrid.CellContains(targetLoc, ThingCategory.Pawn)))
            {
                return false;
            }
            if (this.verbProps.requireLineOfSight)
            {
                // Calculate shot vector
                Vector3 shotSource = ShotSource;

                Vector3 targetPos;
                if (targetThing != null)
                {
                    Vector3 targDrawPos = targetThing.DrawPos;
                    targetPos = new Vector3(targDrawPos.x, new CollisionVertical(targetThing).Max, targDrawPos.z);
                    var targPawn = targetThing as Pawn;
                    if (targPawn != null)
                    {
                        targetPos += targPawn.Drawer.leaner.LeanOffset * 0.5f;
                    }
                }
                else
                {
                    targetPos = targetLoc.ToVector3Shifted();
                }
                Ray shotLine = new Ray(shotSource, (targetPos - shotSource));

                // Create validator to check for intersection with partial cover
                var aimMode = CompFireModes?.CurrentAimMode;
                Func<IntVec3, bool> validator = delegate (IntVec3 cell)
                {
                    // Skip this check entirely if we're doing suppressive fire and cell is adjacent to target
                    if (VerbPropsCE.ignorePartialLoSBlocker || aimMode == AimMode.SuppressFire)
                    {
                        return true;
                    }

                    Thing cover = cell.GetFirstPawn(caster.Map);

                    if (cover == null)
                    {
                        cover = cell.GetCover(caster.Map);
                    }
					
                    if (cover != null && cover != ShooterPawn && cover != caster && cover != targetThing && !cover.IsPlant())
                    {
                    //    var tr = cover.Position.AdjacentTo8Way(sourceSq);
                    //    Log.Message("cover: " + cover.ToString() + " value: " + tr.ToString());

                        if (VerbPropsCE.verbClass == typeof(Verb_ShootCEOneUse) && cover.def.fillPercent < 0.8)
                        {
                            return true;
                        }

                        Bounds bounds = CE_Utility.GetBoundsFor(cover);
                        // Check for intersect
                        if (bounds.IntersectRay(shotLine))
                        {
                            if (Controller.settings.DebugDrawPartialLoSChecks) caster.Map.debugDrawer.FlashCell(cell, 0, bounds.size.y.ToString());
                            return false;
                        }
                        else if (Controller.settings.DebugDrawPartialLoSChecks)
                        {
                            caster.Map.debugDrawer.FlashCell(cell, 0.7f, bounds.size.y.ToString());
                        }

                        if (cover.def.fillPercent > 0.99)
                        {
                            return false;
                        }
                    }
                    return true;
                };
                // Add validator to parameters
                /*
                if (!includeCorners)
                {
                    if (!GenSight.LineOfSight(sourceSq, targetLoc, this.caster.Map, true, validator, 0, 0))
                    {
                        Log.Message("4.1 CanHitCellFromCellIgnoringRange: false");
                        return false;
                    }
                }
                else if (!GenSight.LineOfSightToEdges(sourceSq, targetLoc, this.caster.Map, true, validator))
                {
                    Log.Message("4.2 CanHitCellFromCellIgnoringRange: false");
                    return false;
                }
                */
                var exactTargetSq = targetPos.ToIntVec3();
                foreach (IntVec3 curCell in GenSight.PointsOnLineOfSight(sourceSq, exactTargetSq))
                {
                    if (curCell != sourceSq && curCell != exactTargetSq && !validator(curCell))
                    {
                        return false;
                    }
                }
                int c = 0;
                foreach (IntVec3 curCell2 in GenSight.PointsOnLineOfSight(rootCell, exactTargetSq))
                {
                    if (curCell2 != rootCell && curCell2 != exactTargetSq && !validator(curCell2))
                    {
                        c++;
                    }
                    if (c >= 2)
                    {
                        if (rootCell.z == exactTargetSq.z || rootCell.x == exactTargetSq.x)
                        {
                            return false;
                        }
                    }
                }
            }
            return true;
        }

        #endregion

        #endregion
    }
}
