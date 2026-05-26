using Vintagestory.GameContent;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

using HarmonyLib;

using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Generic;

namespace Scaffolding.Patches;

public static class PlayerPatches
{
    public static ICoreAPI Api;
    public static void ApplyAll(Harmony harmony)
    {
        Apply(harmony, typeof(EntityBehaviorControlledPhysics), "ApplyTests", transpiler: nameof(ClimbingTranspiler));
        Apply(harmony, typeof(CollisionTester), "ApplyTerrainCollision", transpiler: nameof(WalkingTranspiler));
    }

    private static void Apply(Harmony harmony, System.Type target, string function, string prefix = null, string postfix = null, string transpiler = null)
    {
        MethodInfo h_target = AccessTools.Method(target, function);

        MethodInfo h_prefix = prefix != null ? AccessTools.Method(typeof(PlayerPatches), prefix) : null;
        MethodInfo h_postfix = postfix != null ? AccessTools.Method(typeof(PlayerPatches), postfix) : null;
        MethodInfo h_transpiler = transpiler != null ? AccessTools.Method(typeof(PlayerPatches), transpiler) : null;

        harmony.Patch(h_target,
            prefix: h_prefix != null ? new HarmonyMethod(h_prefix) : null,
            postfix: h_postfix != null ? new HarmonyMethod(h_postfix) : null,
            transpiler: h_transpiler != null ? new HarmonyMethod(h_transpiler) : null);
    }

    private static IEnumerable<CodeInstruction> ClimbingTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var codes = new List<CodeInstruction>(instructions);
        var getCollisionBoxes = AccessTools.Method(typeof(Block), nameof(Block.GetCollisionBoxes), new[] { typeof(IBlockAccessor), typeof(BlockPos) });
        var injectMethod = AccessTools.Method(typeof(PlayerPatches), nameof(InjectCustomCollisionBoxes));

        var capturedBlock = generator.DeclareLocal(typeof(Block));

        // Pre-scan: find all GetCollisionBoxes call sites and identify which ones
        // have a method call (e.g. GetBlock) as the block source
        var methodCallSources = new HashSet<int>();
        var collisionCallIndices = new List<int>();

        for (int i = 3; i < codes.Count; i++)
        {
            if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo mi && mi == getCollisionBoxes)
            {
                collisionCallIndices.Add(i);
                var blockLoad = codes[i - 3];
                if (blockLoad.opcode == OpCodes.Call || blockLoad.opcode == OpCodes.Callvirt)
                {
                    methodCallSources.Add(i - 3);
                }
            }
        }

        // The second GetCollisionBoxes is a lookahead collision check — skip it
        var skipIndices = new HashSet<int>();
        if (collisionCallIndices.Count >= 2)
            skipIndices.Add(collisionCallIndices[1]);

        for (int i = 0; i < codes.Count; i++)
        {
            yield return codes[i];

            if (methodCallSources.Contains(i))
            {
                yield return new CodeInstruction(OpCodes.Dup);
                yield return new CodeInstruction(OpCodes.Stloc, capturedBlock);
            }

            if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo mi
                && mi == getCollisionBoxes && !skipIndices.Contains(i))
            {
                if (methodCallSources.Contains(i - 3))
                    yield return new CodeInstruction(OpCodes.Ldloc, capturedBlock);
                else
                    yield return new CodeInstruction(codes[i - 3].opcode, codes[i - 3].operand);

                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldloc_0);
                yield return new CodeInstruction(OpCodes.Ldarg_2);
                yield return new CodeInstruction(OpCodes.Call, injectMethod);
            }
        }
    }

    public static Cuboidf[] InjectCustomCollisionBoxes(Cuboidf[] original, Block block, object instance, Entity entity, EntityControls controls)
    {
        if (instance is not EntityBehaviorPlayerPhysics) return original;
        if (block == null || entity == null || controls == null) return original;
        if (!block.WildCardMatch("scaffolding-*-*")) return original;

        controls.IsClimbing = true;
        entity.ClimbingOnFace = null;

        return original;
    }

    private static IEnumerable<CodeInstruction> WalkingTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        var generateCollisionBoxList = AccessTools.Method(typeof(CollisionTester), "GenerateCollisionBoxList");
        var injectMethod = AccessTools.Method(typeof(PlayerPatches), nameof(InjectCustomTerrainCollisionBoxes));

        for (int i = 0; i < codes.Count; i++)
        {
            var code = codes[i];
            yield return code;

            // Look for the first callvirt to GetCollisionBoxes
            if (code.opcode == OpCodes.Callvirt && code.operand is MethodInfo mi && mi == generateCollisionBoxList)
            {
                yield return new CodeInstruction(OpCodes.Ldloc_0); // WorldAccessor
                yield return new CodeInstruction(OpCodes.Callvirt, AccessTools.Method(typeof(IWorldAccessor), "get_BlockAccessor")); // blockAccessor
                yield return new CodeInstruction(OpCodes.Ldarg_0); // CollisionTester
                yield return new CodeInstruction(OpCodes.Ldarg_1); // entity

                yield return new CodeInstruction(OpCodes.Call, injectMethod);
            }
        }
    }

    /// <summary>
    /// Receives the original collision boxes and the block instance
    /// </summary>
    public static void InjectCustomTerrainCollisionBoxes(IBlockAccessor blockAccessor, CollisionTester tester, Entity entity)
    {
        if (entity is EntityPlayer ec)
        {
            if (ec.Controls.IsClimbing) return;
            if (ec.Controls.Sneak) return;

            blockAccessor.WalkBlocks(tester.minPos, tester.maxPos, (block, x, y, z) =>
                {
                    if (block?.WildCardMatch("scaffolding-*-*") == true)
                    {
                        tester.CollisionBoxList.Add(block.SelectionBoxes, x, y, z, block);
                    }
                }, true);
        }
        else if (entity is EntityItem)
        {
            blockAccessor.WalkBlocks(tester.minPos, tester.maxPos, (block, x, y, z) =>
                {
                    if (block?.WildCardMatch("scaffolding-*-*") == true)
                    {
                        tester.CollisionBoxList.Add(block.SelectionBoxes, x, y, z, block);
                    }
                }, true);
        }
    }
}
