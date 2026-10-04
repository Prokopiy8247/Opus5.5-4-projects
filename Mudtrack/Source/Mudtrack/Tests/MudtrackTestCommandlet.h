// MudtrackTestCommandlet.h
// Headless entry point for the physics protocol tests.
//
// Run with:
//   UnrealEditor-Cmd.exe <project>.uproject -run=MudtrackTests -unattended -nosplash
//
// It creates a game world in-process, spawns the soil field, the test course and
// the vehicle, then drives the whole protocol test suite and writes the measured
// results to Saved/MudtrackTests/results.json. This is what backs the numbers in
// FINAL_REPORT.md.

#pragma once

#include "CoreMinimal.h"
#include "Commandlets/Commandlet.h"
#include "MudtrackTestCommandlet.generated.h"

UCLASS()
class MUDTRACK_API UMudtrackTestsCommandlet : public UCommandlet
{
	GENERATED_BODY()

public:
	UMudtrackTestsCommandlet();

	virtual int32 Main(const FString& Params) override;
};

