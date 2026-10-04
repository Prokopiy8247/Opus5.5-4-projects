// Copyright Epic Games, Inc. All Rights Reserved.

using UnrealBuildTool;

public class Mudtrack : ModuleRules
{
	public Mudtrack(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;

		PublicDependencyModuleNames.AddRange(new string[]
		{
			"Core",
			"CoreUObject",
			"Engine",
			"InputCore",
			"EnhancedInput",
			// Runtime mesh for the deformable terrain surface
			"ProceduralMeshComponent",
			// Physics types and the physical-material surface enum
			"PhysicsCore",
			"Chaos",
			"ChaosCore",
		});

		PrivateDependencyModuleNames.AddRange(new string[]
		{
			"RenderCore",
			"RHI",
			"Slate",
			"SlateCore",
			// Test results are serialised to JSON
			"Json",
		});

		// The soil solver uses per-cell math in tight loops; keep optimisation on
		// even in Development so the physics budget is representative.
		if (Target.Configuration != UnrealTargetConfiguration.Debug)
		{
			PrivateDefinitions.Add("MUDTRACK_OPTIMIZED=1");
		}
	}
}
