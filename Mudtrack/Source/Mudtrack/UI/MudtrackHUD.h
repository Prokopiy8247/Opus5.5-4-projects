// MudtrackHUD.h
// The player-facing HUD and the technical diagnostics overlay.
//
// Everything drawn here comes from real solver state: wheel loads, slip, sinkage
// and rut depth are read from the vehicle, physics timestep from the pawn's own
// measurement. Nothing is a canned readout.

#pragma once

#include "CoreMinimal.h"
#include "GameFramework/HUD.h"
#include "MudtrackHUD.generated.h"

class AOffroadVehiclePawn;

UCLASS()
class MUDTRACK_API AMudtrackHUD : public AHUD
{
	GENERATED_BODY()

public:
	AMudtrackHUD();

	virtual void DrawHUD() override;
	virtual void Tick(float DeltaSeconds) override;

	/** Toggle the technical overlay (bound to a key, and callable from tests). */
	UFUNCTION(BlueprintCallable, Category = "HUD")
	void ToggleDiagnostics() { bShowDiagnostics = !bShowDiagnostics; }

	UFUNCTION(BlueprintCallable, Category = "HUD")
	void SetDiagnosticsVisible(bool bVisible) { bShowDiagnostics = bVisible; }

	UPROPERTY(BlueprintReadOnly, Category = "HUD")
	bool bShowDiagnostics = false;

	/**
	 * Layout height the HUD's pixel sizes were chosen against, in pixels.
	 *
	 * Every offset and font size here is a fixed number of pixels, so without
	 * this the HUD keeps its size as the viewport grows: at 2560x1440 the text
	 * came out at a third of its intended size and was unreadable, while at the
	 * 640x480 the tests run at it was oversized. The whole layout is multiplied
	 * by the viewport's height over this reference.
	 */
	UPROPERTY(EditDefaultsOnly, BlueprintReadOnly, Category = "HUD")
	float ReferenceHeight = 720.f;

	/** Toggle the on-screen key list (the block along the bottom-left). */
	UPROPERTY(EditDefaultsOnly, BlueprintReadOnly, Category = "HUD")
	bool bShowControlHints = true;

	/** Current viewport scale, recomputed each frame by DrawHUD. */
	float UIScale = 1.f;

protected:
	void DrawMainPanel(AOffroadVehiclePawn* V);
	void DrawDiagnosticsPanel(AOffroadVehiclePawn* V);
	void DrawCrosshairInfo(AOffroadVehiclePawn* V);

	/** Draw one HUD text item, scaled to the viewport. */
	void DrawLine(const FString& Text, float X, float& Y, float LH,
	              const FLinearColor& Colour, UFont* Font);

	/** Smoothed FPS, so the readout is not a jittering number. */
	float SmoothedFPS = 0.f;

	/** Rolling average of the vehicle physics cost, milliseconds. */
	float SmoothedPhysicsMs = 0.f;

	UPROPERTY()
	TObjectPtr<AOffroadVehiclePawn> CachedVehicle;
};
