// MudtrackTestCommandlet.cpp
//
// This commandlet deliberately does NOT run the protocol suite.
//
// The suite is frame-stepped and needs a real engine game loop, because a world
// built inside a commandlet does not advance the Chaos physics scene: UWorld::Tick
// alone never integrates the rigid bodies, so every measurement returns its spawn
// value. Driving FPhysScene_Chaos by hand is not a workaround either, since its
// EndFrame asserts unless Chaos's asynchronous solve has already completed.
//
// The tests therefore run inside a genuine game session, launched like this:
//
//   UnrealEditor-Cmd.exe <project>.uproject /Game/Mudtrack/Maps/MudtrackForest ^
//       -game -MudtrackRunTests -windowed -ResX=640 -ResY=480 -nosplash
//
// AMudtrackGameMode sees -MudtrackRunTests, spawns ATestDriver, and the driver
// advances the suite one step per frame, then writes Saved/MudtrackTests/results.json
// and exits with a status reflecting the outcome.
//
// This commandlet remains as an explicit, honest no-op that points at the right
// invocation rather than silently reporting success.

#include "MudtrackTestCommandlet.h"
#include "MudtrackTests.h"
#include "../Mudtrack.h"

DEFINE_LOG_CATEGORY_STATIC(LogMudCmdlet, Log, All);

UMudtrackTestsCommandlet::UMudtrackTestsCommandlet()
{
	IsClient = false;
	IsServer = true;
	IsEditor = true;
	LogToConsole = true;
}

int32 UMudtrackTestsCommandlet::Main(const FString& Params)
{
	UE_LOG(LogMudCmdlet, Display, TEXT("=== Mudtrack protocol tests ==="));
	UE_LOG(LogMudCmdlet, Display,
		TEXT("This commandlet does not run the suite: a commandlet world does not"));
	UE_LOG(LogMudCmdlet, Display,
		TEXT("advance the Chaos physics scene, so the measurements would be meaningless."));
	UE_LOG(LogMudCmdlet, Display, TEXT("Run the tests inside a real game session instead:"));
	UE_LOG(LogMudCmdlet, Display, TEXT(""));
	UE_LOG(LogMudCmdlet, Display,
		TEXT("  UnrealEditor-Cmd.exe <project>.uproject /Game/Mudtrack/Maps/MudtrackForest"));
	UE_LOG(LogMudCmdlet, Display,
		TEXT("      -game -MudtrackRunTests -windowed -ResX=640 -ResY=480 -nosplash"));
	UE_LOG(LogMudCmdlet, Display, TEXT(""));
	UE_LOG(LogMudCmdlet, Display,
		TEXT("Results are written to Saved/MudtrackTests/results.json"));

	// Return failure so a caller cannot mistake this for a passing test run.
	return 1;
}
