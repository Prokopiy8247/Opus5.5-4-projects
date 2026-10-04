// MudtrackHUD.cpp

#include "MudtrackHUD.h"
#include "../Vehicle/OffroadVehiclePawn.h"
#include "Engine/Canvas.h"
#include "Engine/Engine.h"
#include "Engine/Font.h"
#include "CanvasItem.h"
#include "GameFramework/PlayerController.h"

AMudtrackHUD::AMudtrackHUD()
{
	PrimaryActorTick.bCanEverTick = true;
}

void AMudtrackHUD::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);

	if (DeltaSeconds > KINDA_SMALL_NUMBER)
	{
		const float Inst = 1.f / DeltaSeconds;
		SmoothedFPS = SmoothedFPS <= 0.f ? Inst : FMath::Lerp(SmoothedFPS, Inst, 0.08f);
	}

	if (CachedVehicle == nullptr)
	{
		if (APlayerController* PC = GetOwningPlayerController())
		{
			CachedVehicle = Cast<AOffroadVehiclePawn>(PC->GetPawn());
		}
	}

	if (CachedVehicle != nullptr)
	{
		SmoothedPhysicsMs = SmoothedPhysicsMs <= 0.f
			? CachedVehicle->LastPhysicsMs
			: FMath::Lerp(SmoothedPhysicsMs, CachedVehicle->LastPhysicsMs, 0.1f);
	}
}

void AMudtrackHUD::DrawHUD()
{
	Super::DrawHUD();

	AOffroadVehiclePawn* V = CachedVehicle;
	if (V == nullptr)
	{
		if (APlayerController* PC = GetOwningPlayerController())
		{
			V = Cast<AOffroadVehiclePawn>(PC->GetPawn());
			CachedVehicle = V;
		}
	}
	if (V == nullptr || Canvas == nullptr) return;

	// Everything below is written in the pixel sizes the layout was designed at
	// (ReferenceHeight, 720 by default) and scaled to the actual viewport here.
	// Without it the HUD kept its absolute size: tiny and unreadable at 2560x1440,
	// oversized at the 640x480 the tests run at. 0.75 is a floor so a very small
	// window still renders legible text rather than collapsing the layout.
	UIScale = FMath::Clamp(Canvas->ClipY / FMath::Max(ReferenceHeight, 1.f), 0.75f, 3.f);

	DrawMainPanel(V);
	if (bShowDiagnostics)
	{
		DrawDiagnosticsPanel(V);
	}
}

void AMudtrackHUD::DrawLine(const FString& Text, float X, float& Y, float LH,
                            const FLinearColor& Colour, UFont* Font)
{
	FCanvasTextItem It(FVector2D(X * UIScale, Y * UIScale), FText::FromString(Text), Font, Colour);
	It.Scale = FVector2D(UIScale, UIScale);
	It.EnableShadow(FLinearColor::Black);
	Canvas->DrawItem(It);
	Y += LH;
}

void AMudtrackHUD::DrawMainPanel(AOffroadVehiclePawn* V)
{
	UFont* Font = GEngine ? GEngine->GetMediumFont() : nullptr;
	UFont* BigFont = GEngine ? GEngine->GetLargeFont() : nullptr;
	if (Font == nullptr) return;

	const float X = 26.f;
	float Y = 26.f;
	const float LH = 20.f;

	// Speed, large and clear.
	const FString SpeedStr = FString::Printf(TEXT("%.0f"), V->SpeedKph);
	if (BigFont)
	{
		FCanvasTextItem SpeedItem(FVector2D(X * UIScale, Y * UIScale), FText::FromString(SpeedStr), BigFont, FLinearColor(1.f, 1.f, 1.f));
		SpeedItem.Scale = FVector2D(1.7f * UIScale, 1.7f * UIScale);
		SpeedItem.EnableShadow(FLinearColor::Black);
		Canvas->DrawItem(SpeedItem);
	}
	{
		FCanvasTextItem UnitItem(FVector2D((X + 96.f) * UIScale, (Y + 22.f) * UIScale), FText::FromString(TEXT("km/h")), Font, FLinearColor(0.85f, 0.85f, 0.85f));
		UnitItem.Scale = FVector2D(UIScale, UIScale);
		UnitItem.EnableShadow(FLinearColor::Black);
		Canvas->DrawItem(UnitItem);
	}
	Y += 54.f;

	// Gear + range
	const FString GearStr = V->Drive.GearIndex == 0
		? TEXT("R") : FString::Printf(TEXT("%d"), V->Drive.GearIndex);
	const FString RangeStr = V->Drive.bLowRange ? TEXT("LOW") : TEXT("HIGH");
	{
		const FString Line = FString::Printf(TEXT("GEAR %s %s   RANGE %s"), *GearStr,
			V->bAutomaticGearbox ? TEXT("AUTO") : TEXT("MANUAL"), *RangeStr);
		DrawLine(Line, X, Y, LH,
			V->Drive.bLowRange ? FLinearColor(0.95f, 0.75f, 0.25f) : FLinearColor(0.9f, 0.9f, 0.9f), Font);
	}

	// RPM bar
	{
		const float RPMPct = FMath::Clamp(V->Drive.EngineRPM / FMath::Max(V->Engine.MaxRPM, 1.f), 0.f, 1.f);
		const float BarW = 210.f, BarH = 9.f;
		FCanvasTileItem Bg(FVector2D(X * UIScale, (Y + 4.f) * UIScale), FVector2D(BarW, BarH) * UIScale,
			FLinearColor(0.05f, 0.05f, 0.06f, 0.75f));
		Bg.BlendMode = SE_BLEND_Translucent;
		Canvas->DrawItem(Bg);

		FLinearColor BarCol = FLinearColor(0.25f, 0.85f, 0.35f);
		if (RPMPct > 0.82f) BarCol = FLinearColor(0.95f, 0.25f, 0.2f);
		else if (RPMPct > 0.62f) BarCol = FLinearColor(0.95f, 0.8f, 0.25f);

		FCanvasTileItem Bar(FVector2D((X + 1.f) * UIScale, (Y + 5.f) * UIScale),
			FVector2D((BarW - 2.f) * RPMPct, BarH - 2.f) * UIScale, BarCol);
		Bar.BlendMode = SE_BLEND_Translucent;
		Canvas->DrawItem(Bar);

		const FString RPMStr = FString::Printf(TEXT("%.0f rpm"), V->Drive.EngineRPM);
		FCanvasTextItem It(FVector2D((X + BarW + 12.f) * UIScale, Y * UIScale), FText::FromString(RPMStr), Font, FLinearColor(0.9f, 0.9f, 0.9f));
		It.Scale = FVector2D(UIScale, UIScale);
		It.EnableShadow(FLinearColor::Black);
		Canvas->DrawItem(It);
	}
	Y += LH + 6.f;

	// Drivetrain line
	DrawLine(V->GetDrivetrainSummary(), X, Y, LH, FLinearColor(0.8f, 0.88f, 1.f), Font);

	// Handbrake / spinning indicators
	if (V->bHandbrakeOn)
	{
		DrawLine(TEXT("HANDBRAKE"), X, Y, LH, FLinearColor(0.95f, 0.3f, 0.3f), Font);
	}

	bool bAnySpin = false;
	for (const FWheelState& S : V->WheelStates) { if (S.bSpinning) bAnySpin = true; }
	if (bAnySpin)
	{
		DrawLine(TEXT("WHEELSPIN"), X, Y, LH, FLinearColor(0.95f, 0.7f, 0.2f), Font);
	}

	// Winch status
	{
		const FLinearColor WC = V->bWinchAttached ? FLinearColor(0.4f, 0.9f, 0.5f) : FLinearColor(0.6f, 0.6f, 0.65f);
		const FString WStr = V->bWinchAttached
			? FString::Printf(TEXT("WINCH %.2f kN  %.1f m"), V->WinchForce / 1000.f, V->WinchCableLength / 100.f)
			: FString(TEXT("WINCH ready"));
		DrawLine(WStr, X, Y, LH, WC, Font);
	}

	if (!V->WinchMessage.IsEmpty())
	{
		DrawLine(V->WinchMessage, X, Y, LH, FLinearColor(0.95f, 0.85f, 0.45f), Font);
	}

	// Controls hint, bottom-left
	if (bShowControlHints)
	{
		const TArray<FString> Hints = {
			TEXT("W/S drive & brake (S reverses when stopped)   A/D steer   Space handbrake"),
			TEXT("Q/E gear (manual)   M auto gearbox   R range low/high   1 drive mode   2 centre diff"),
			TEXT("3/4 rear & front diff   F winch   G release   T recover at base"),
			TEXT("C camera   Tab diagnostics   PgUp/PgDn test load"),
		};
		// Anchored to the bottom edge, so the block rises with the viewport instead
		// of drifting away from it.
		float HY = Canvas->ClipY / UIScale - 24.f - 18.f * Hints.Num();
		for (const FString& H : Hints)
		{
			DrawLine(H, X, HY, 18.f, FLinearColor(0.72f, 0.76f, 0.8f, 0.85f), Font);
		}
	}
}

void AMudtrackHUD::DrawDiagnosticsPanel(AOffroadVehiclePawn* V)
{
	UFont* Font = GEngine ? GEngine->GetMediumFont() : nullptr;
	if (Font == nullptr || Canvas == nullptr) return;

	const float PanelW = 470.f;
	// Anchored to the right edge of the viewport, and never allowed to start left
	// of the panel edge on a narrow window.
	const float X = FMath::Max(Canvas->ClipX / UIScale - PanelW - 26.f, 12.f);
	float Y = 26.f;
	const float LH = 17.f;

	// The panel grows with its content; the previous fixed 34-line box was sized
	// for this list and would clip the moment a line was added.
	const int32 LineEstimate = 34;

	// Panel background
	{
		FCanvasTileItem Bg(FVector2D((X - 12.f) * UIScale, (Y - 10.f) * UIScale),
			FVector2D(PanelW, 12.f + LH * LineEstimate) * UIScale,
			FLinearColor(0.02f, 0.03f, 0.05f, 0.72f));
		Bg.BlendMode = SE_BLEND_Translucent;
		Canvas->DrawItem(Bg);
	}

	auto Line = [&](const FString& S, const FLinearColor& C)
	{
		DrawLine(S, X, Y, LH, C, Font);
	};

	Line(TEXT("--- PHYSICS DIAGNOSTICS ---"), FLinearColor(0.55f, 0.85f, 1.f));
	Line(FString::Printf(TEXT("FPS %.1f   physics %.2f ms   substeps %d"),
		SmoothedFPS, SmoothedPhysicsMs, V->CurrentSubsteps), FLinearColor(0.85f, 0.85f, 0.9f));
	Line(FString::Printf(TEXT("speed %.1f km/h   pitch %.1f   roll %.1f"),
		V->SpeedKph, V->BodyPitchDeg, V->BodyRollDeg), FLinearColor(0.85f, 0.85f, 0.9f));
	Line(FString::Printf(TEXT("mass %.0f kg (load %.0f kg)   CoM %s"),
		V->MassKg + V->TestLoadKg, V->TestLoadKg,
		*V->CentreOfMassOffset.ToCompactString()), FLinearColor(0.85f, 0.85f, 0.9f));
	Line(FString::Printf(TEXT("engine %.0f rpm   torque %.1f Nm"),
		V->Drive.EngineRPM, V->Drive.EngineTorqueNcm / 100.f), FLinearColor(0.85f, 0.85f, 0.9f));
	Y += 6.f;

	Line(TEXT("  whl  grnd  susp   load      slip   slipA   sink    rut   grip"),
		FLinearColor(0.6f, 0.8f, 0.95f));

	const TCHAR* Names[4] = { TEXT("FL"), TEXT("FR"), TEXT("RL"), TEXT("RR") };
	for (int32 i = 0; i < V->WheelStates.Num() && i < 4; ++i)
	{
		const FWheelState& S = V->WheelStates[i];
		const FLinearColor C = S.bGrounded
			? (S.bSpinning ? FLinearColor(0.98f, 0.72f, 0.3f) : FLinearColor(0.82f, 0.86f, 0.9f))
			: FLinearColor(0.55f, 0.55f, 0.6f);

		Line(FString::Printf(TEXT("  %s  %s  %5.1f  %7.0f  %6.2f  %6.1f  %5.1f  %5.1f  %4.2f"),
			Names[i],
			S.bGrounded ? TEXT("YES") : TEXT("AIR"),
			S.SuspensionLength,
			S.NormalLoad,
			S.SlipRatio,
			FMath::RadiansToDegrees(S.SlipAngle),
			S.Sinkage,
			S.RutDepth,
			S.TractionScale),
			C);
	}

	Y += 6.f;
	// Soil class under each wheel
	{
		auto SoilName = [](ESoilClass C) -> const TCHAR*
		{
			switch (C)
			{
			case ESoilClass::FirmRoad: return TEXT("road");
			case ESoilClass::DrySoil:  return TEXT("dry");
			case ESoilClass::WetSoil:  return TEXT("wet");
			case ESoilClass::DeepMud:  return TEXT("MUD");
			case ESoilClass::Gravel:   return TEXT("gravel");
			case ESoilClass::Rock:     return TEXT("rock");
			case ESoilClass::WaterBed: return TEXT("water");
			default:                   return TEXT("?");
			}
		};
		FString SS = TEXT("soil:");
		for (int32 i = 0; i < V->WheelStates.Num() && i < 4; ++i)
		{
			SS += FString::Printf(TEXT(" %s=%s"), Names[i], SoilName(V->WheelStates[i].SoilClass));
		}
		Line(SS, FLinearColor(0.8f, 0.8f, 0.7f));
	}

	Line(FString::Printf(TEXT("winch: %s  force %.0f N  cable %.0f cm"),
		V->bWinchAttached ? TEXT("ATTACHED") : TEXT("idle"), V->WinchForce, V->WinchCableLength),
		FLinearColor(0.8f, 0.9f, 0.8f));

	Line(TEXT("--- SOIL FIELD ---"), FLinearColor(0.55f, 0.85f, 1.f));
	// The field stats are filled in by the game mode / field itself.
	Line(FString::Printf(TEXT("TAB to hide   camera mode: %s"),
		V->CameraMode == 0 ? TEXT("chase") : (V->CameraMode == 1 ? TEXT("suspension") : TEXT("free"))),
		FLinearColor(0.75f, 0.75f, 0.8f));
}
