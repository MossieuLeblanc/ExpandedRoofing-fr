﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Verse;
using RimWorld;
using Harmony;

namespace ExpandedRoofing
{
    static class Helper
    {
        public static bool CheckTransparency(GlowGrid gg, Map map, IntVec3 c, ref float num)
        {
            RoofExtension transparentRoofExt = map.roofGrid.RoofAt(c)?.GetModExtension<RoofExtension>();
            if (transparentRoofExt != null)
            {
                num = map.skyManager.CurSkyGlow * transparentRoofExt.transparency;
                if (num == 1f) return true;
            }
            return false;
        }

        private static int KillFinalize(int count)
        {
            return GenMath.RoundRandom((float)count * 0.5f);
        }

        // NOTE: consider destruction mode for better spawning
        public static void DoLeavings(RoofDef curRoof, ThingDef spawnerDef, Map map, CellRect leavingsRect)
        {
            ThingOwner<Thing> thingOwner = new ThingOwner<Thing>();
            ThingDef stuff = null;
            string stuffDefName = curRoof.defName.Replace("ThickStoneRoof", "");
            if(stuffDefName == "Jade") stuff = DefDatabase<ThingDef>.GetNamed(stuffDefName, false);
            else stuff = DefDatabase<ThingDef>.GetNamed($"Blocks{stuffDefName}", false);

            List<ThingCountClass> thingCounts = spawnerDef.CostListAdjusted(stuff, true);

            foreach (ThingCountClass curCntCls in thingCounts)
            {
                int val = KillFinalize(curCntCls.count);
                if (val > 0)
                {
                    Thing thing = ThingMaker.MakeThing(curCntCls.thingDef, null);
                    thing.stackCount = val;
                    thingOwner.TryAdd(thing, true);
                }
            }

            // TODO: rewrite this later...
            List<IntVec3> list = leavingsRect.Cells.InRandomOrder(null).ToList<IntVec3>();
            int num = 0;
            while (thingOwner.Count > 0)
            {
                /*if (mode == DestroyMode.KillFinalize && !map.areaManager.Home[list2[num4]])
                {
                    thingOwner[0].SetForbidden(true, false);
                }*/
                if (!thingOwner.TryDrop(thingOwner[0], list[num], map, ThingPlaceMode.Near, out Thing thing, null))
                {
                    Log.Warning(string.Concat(new object[] { "Failed to place all leavings for destroyed thing ", curRoof, " at ", leavingsRect.CenterCell }));
                    return;
                }
                if (++num >= list.Count) num = 0;
            }

        }

        public static bool SkipRoofRendering(RoofDef roofDef)
        {
            return roofDef == RoofDefOf.RoofTransparent;
        }
    }

    [StaticConstructorOnStartup]
    internal class HarmonyPatches
    {
        static MethodInfo MI_CheckTransparency = AccessTools.Method(typeof(Helper), nameof(Helper.CheckTransparency));
        static MethodInfo MI_SkipRoofRendering = AccessTools.Method(typeof(Helper), nameof(Helper.SkipRoofRendering));
        static FieldInfo FI_GlowGrid_map = AccessTools.Field(typeof(GlowGrid), "map");
        static FieldInfo FI_RoofGrid_map = AccessTools.Field(typeof(RoofGrid), "map");
        static MethodInfo MI_getDestroyed = AccessTools.Property(typeof(Thing), nameof(Thing.Destroyed)).GetGetMethod();
        static MethodInfo MI_RoofAt = AccessTools.Method(typeof(RoofGrid), nameof(RoofGrid.RoofAt), new[] { typeof(int), typeof(int) });

        static HarmonyPatches()
        {
#if DEBUG
            HarmonyInstance.DEBUG = true;
#endif
            HarmonyInstance harmony = HarmonyInstance.Create("rimworld.whyisthat.expandedroofing.main");

            // correct lighting for plant growth
            harmony.Patch(AccessTools.Method(typeof(GlowGrid), nameof(GlowGrid.GameGlowAt)), null, null, new HarmonyMethod(typeof(HarmonyPatches), nameof(GameGlowTranspiler)));

            // set roof to return materials
            harmony.Patch(AccessTools.Method(typeof(RoofGrid), nameof(RoofGrid.SetRoof)), new HarmonyMethod(typeof(HarmonyPatches), nameof(SetRoofPrefix)), null);

            // fix lighting inside rooms with transparent roof  
            harmony.Patch(AccessTools.Method(typeof(SectionLayer_LightingOverlay), nameof(SectionLayer_LightingOverlay.Regenerate)), null, null, new HarmonyMethod(typeof(HarmonyPatches), nameof(RegenerateTranspiler)));

            // Allow roof frames to be built above things (e.g. trees)
            harmony.Patch(AccessTools.Method(typeof(Blueprint), nameof(Blueprint.FirstBlockingThing)), new HarmonyMethod(typeof(HarmonyPatches), nameof(FirstBlockingThingPrefix)), null);
        }

        public static bool FirstBlockingThingPrefix(Blueprint __instance)
        {
            ThingDef thingDef = __instance.def.entityDefToBuild as ThingDef;
            if (thingDef?.HasComp(typeof(CompAddRoof)) == true) return false;
            return true;
        }

        /*public static bool BlocksFramePlacementPrefix(Blueprint blue, Thing t)
        {
            ThingDef thingDef = blue.def.entityDefToBuild as ThingDef;
            if (thingDef?.HasComp(typeof(CompProperties_AddRoof)) == true) return false;
            return true;
        }*/

        public static IEnumerable<CodeInstruction> GameGlowTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            List<CodeInstruction> instructionList = instructions.ToList();
            int i;
            for (i = 0; i < instructionList.Count; i++)
            {
                yield return instructionList[i];
                if (instructionList[i].opcode == OpCodes.Ret)
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0) { labels = instructionList[++i].labels };
                    yield return new CodeInstruction(OpCodes.Dup);
                    yield return new CodeInstruction(OpCodes.Ldfld, FI_GlowGrid_map);
                    yield return new CodeInstruction(OpCodes.Ldarg_1);
                    yield return new CodeInstruction(OpCodes.Ldloca, 0);
                    yield return new CodeInstruction(OpCodes.Call, MI_CheckTransparency);
                    //yield return new CodeInstruction(OpCodes.Stloc_0);
                    //yield return new CodeInstruction(OpCodes.Ldloc_0);
                    //yield return new CodeInstruction(OpCodes.Ldc_R4, 1f);
                    Label @continue = il.DefineLabel();
                    yield return new CodeInstruction(OpCodes.Brfalse, @continue);
                    yield return new CodeInstruction(OpCodes.Ldloc_0);
                    yield return new CodeInstruction(OpCodes.Ret);
                    yield return new CodeInstruction(instructionList[i].opcode, instructionList[i].operand) { labels = { @continue } };
                    break;
                }
            }
            for (i += 1 ; i < instructionList.Count; i++) yield return instructionList[i]; // finish off instructions
        }

        public static void SetRoofPrefix(RoofGrid __instance, IntVec3 c, RoofDef def)
        {
            RoofDef curRoof = __instance.RoofAt(c);
            if (curRoof != null && def != curRoof)
            {
                RoofExtension roofExt = curRoof.GetModExtension<RoofExtension>();
                if (roofExt != null) Helper.DoLeavings(curRoof, roofExt.spawnerDef, FI_RoofGrid_map.GetValue(__instance) as Map, GenAdj.OccupiedRect(c, Rot4.North, roofExt.spawnerDef.size));
            }
        }

        public static IEnumerable<CodeInstruction> RegenerateTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            List<CodeInstruction> instructionList = instructions.ToList();
            int i;
            for (i = 0; i < instructionList.Count; i++)
            {
                yield return instructionList[i];
                if (instructionList[i].opcode == OpCodes.Callvirt && instructionList[i].operand == MI_RoofAt)
                {
                    // NOTE: consider finding a better way to locate this...
                    // make sure state by checking ops a few times
                    yield return instructionList[++i];
                    if (instructionList[i].opcode != OpCodes.Stloc_S) break;

                    yield return instructionList[++i];
                    if (instructionList[i].opcode != OpCodes.Ldloc_S) break;

                    CodeInstruction load = new CodeInstruction(instructionList[i].opcode, instructionList[i].operand);

                    yield return instructionList[++i];
                    if (instructionList[i].opcode != OpCodes.Brfalse) break;

                    yield return load;
                    yield return new CodeInstruction(OpCodes.Call, MI_SkipRoofRendering);
                    Label @continue = il.DefineLabel();
                    yield return new CodeInstruction(OpCodes.Brtrue, @continue);
                    while (instructionList[++i].opcode != OpCodes.Stloc_S) { yield return instructionList[i]; } // yield block
                    yield return instructionList[i++];
                    instructionList[i].labels.Add(@continue);
                    yield return instructionList[i];
                }
            }
        }

    }
}
