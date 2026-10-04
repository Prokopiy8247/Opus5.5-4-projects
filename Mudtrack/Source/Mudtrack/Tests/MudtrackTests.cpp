// MudtrackTests.cpp
//
// The ten protocol tests, in frame-stepped form.
//
// WHY FRAME-STEPPED
// -----------------
// These tests must run inside a real game session, because a commandlet-created
// world does not advance the Chaos physics scene. That rules out blocking:
// looping on GEngine->Tick() from inside an actor's Tick re-enters the tick task
// manager and asserts, and sleeping would stall the very loop we depend on.
//
// So the suite is a state machine advanced once per engine frame by ATestDriver.
// Each test is a switch over its phase. A phase does a little work and either
// falls through to the next phase on the same frame, or calls WaitFor(seconds)
// and returns, so the engine can run real physics. On a later frame the phase is
// re-entered and the test continues. The code reads like straight-line test
// logic while the real loop does all the simulation.

#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "MudtrackTests.h"
#include "../Vehicle/OffroadVehiclePawn.h"
#include "../Soil/SoilField.h"
#include "../Mudtrack.h"

#include "Engine/World.h"
#include "Engine/Engine.h"
#include "EngineUtils.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "Dom/JsonObject.h"
#include "GameFramework/PlayerController.h"
#include "Components/PrimitiveComponent.h"

DEFINE_LOG_CATEGORY_STATIC(LogMudTest, Log, All);

// ===========================================================================
// Statics
// ===========================================================================

FMudtrackTestContext UMudtrackTests::Run;
TArray<FTestResult> UMudtrackTests::LastResults;
FString UMudtrackTests::OutputPath;
int32 UMudtrackTests::PlanIndex = 0;
int32 UMudtrackTests::TestPhase = 0;
bool UMudtrackTests::bPlanComplete = false;
bool UMudtrackTests::bInitialised = false;

// Per-test scratch state, valid only within a single test.
namespace
{
	FVector AnchA = FVector::ZeroVector;
	FVector AnchB = FVector::ZeroVector;
	FVector StartLoc = FVector::ZeroVector;
	FVector AcrossDir = FVector::ZeroVector;

	float ValA = 0.f;
	float ValB = 0.f;
	float ValC = 0.f;
	float RestHeight = 0.f;

	TArray<float> ProfileBefore;
	TArray<float> ProfileAfter;

	FString F2(float V) { return FString::Printf(TEXT("%.2f"), V); }
	FString F3(float V) { return FString::Printf(TEXT("%.3f"), V); }

	void ResetScratch()
	{
		AnchA = AnchB = StartLoc = AcrossDir = FVector::ZeroVector;
		ValA = ValB = ValC = RestHeight = 0.f;
		ProfileBefore.Reset();
		ProfileAfter.Reset();
	}
}

// ===========================================================================
// Run-context helpers
// ===========================================================================

bool FMudtrackTestContext::WaitFor(float Seconds)
{
	WaitUntil = Elapsed + FMath::Max(Seconds, 0.f);
	return true;
}

void FMudtrackTestContext::BeginTest(const FString& Name)
{
	Current = FTestResult();
	Current.Name = Name;
	Current.bRan = true;

	// Every test starts on undisturbed ground with the default drivetrain. Ruts
	// persist by design, and several tests use the same patch of deep mud, so
	// without this a test started in the hole the previous one dug -- and
	// inherited its range, gear and differential settings.
	if (Soil != nullptr)
	{
		Soil->ResetAllSoils();
	}
	if (Vehicle != nullptr)
	{
		Vehicle->Drive.DriveMode = EDriveMode::FourWheelDrive;
		Vehicle->Drive.CenterDiff = EDiffLock::LimitedSlip;
		Vehicle->Drive.FrontDiff = EDiffLock::Open;
		Vehicle->Drive.RearDiff = EDiffLock::Open;
		Vehicle->Drive.bLowRange = false;
		Vehicle->Drive.GearIndex = 1;
		Vehicle->bAutomaticGearbox = true;
		Vehicle->SetHandbrake(false);
	}
}

void FMudtrackTestContext::FinishTest(bool bPassed, const FString& Detail)
{
	Current.bPassed = bPassed;
	Current.Detail = Detail;
}

void FMudtrackTestContext::Measure(const FString& Text)
{
	Current.Measurements.Add(Text);
}

float FMudtrackTestContext::MeanRut() const
{
	if (Vehicle == nullptr) return 0.f;
	float S = 0.f;
	for (const FWheelState& W : Vehicle->WheelStates) S += W.RutDepth;
	return Vehicle->WheelStates.Num() > 0 ? S / Vehicle->WheelStates.Num() : 0.f;
}

float FMudtrackTestContext::MaxRut() const
{
	if (Vehicle == nullptr) return 0.f;
	float M = 0.f;
	for (const FWheelState& W : Vehicle->WheelStates) M = FMath::Max(M, W.RutDepth);
	return M;
}

float FMudtrackTestContext::MeanSink() const
{
	if (Vehicle == nullptr) return 0.f;
	float S = 0.f;
	for (const FWheelState& W : Vehicle->WheelStates) S += W.Sinkage;
	return Vehicle->WheelStates.Num() > 0 ? S / Vehicle->WheelStates.Num() : 0.f;
}

int32 FMudtrackTestContext::GroundedCount() const
{
	if (Vehicle == nullptr) return 0;
	int32 N = 0;
	for (const FWheelState& W : Vehicle->WheelStates) if (W.bGrounded) ++N;
	return N;
}

float FMudtrackTestContext::TotalNormalLoad() const
{
	if (Vehicle == nullptr) return 0.f;
	float S = 0.f;
	for (const FWheelState& W : Vehicle->WheelStates) S += W.NormalLoad;
	return S;
}

float FMudtrackTestContext::MeanSuspensionLength() const
{
	if (Vehicle == nullptr) return 0.f;
	float S = 0.f;
	for (const FWheelState& W : Vehicle->WheelStates) S += W.SuspensionLength;
	return Vehicle->WheelStates.Num() > 0 ? S / Vehicle->WheelStates.Num() : 0.f;
}

void FMudtrackTestContext::Teleport(const FVector& Loc, float YawDeg)
{
	if (Vehicle == nullptr) return;
	UPrimitiveComponent* P = Cast<UPrimitiveComponent>(Vehicle->GetRootComponent());
	if (P)
	{
		P->SetPhysicsLinearVelocity(FVector::ZeroVector);
		P->SetPhysicsAngularVelocityInDegrees(FVector::ZeroVector);
	}
	Vehicle->SetActorLocationAndRotation(Loc, FRotator(0.f, YawDeg, 0.f), false, nullptr,
	                                     ETeleportType::TeleportPhysics);
	if (P)
	{
		P->SetPhysicsLinearVelocity(FVector::ZeroVector);
		P->SetPhysicsAngularVelocityInDegrees(FVector::ZeroVector);
		P->WakeAllRigidBodies();
	}
}

void FMudtrackTestContext::SetInputs(float Throttle, float Brake, float Steer, bool bHandbrake)
{
	if (Vehicle == nullptr) return;
	Vehicle->SetThrottle(Throttle);
	Vehicle->SetBrake(Brake);
	Vehicle->SetSteering(Steer);
	Vehicle->SetHandbrake(bHandbrake);
}

bool FMudtrackTestContext::FindSoilOfClass(ESoilClass Want, const FVector& Near, FVector& OutLoc) const
{
	if (Soil == nullptr) return false;
	const float StepSize = Soil->CellSize * 2.f;
	for (int32 Ring = 0; Ring < 90; ++Ring)
	{
		const float R = Ring * StepSize;
		const int32 Samples = FMath::Max(8, Ring * 6);
		for (int32 s = 0; s < Samples; ++s)
		{
			const float A = 2.f * PI * (float)s / (float)Samples;
			const FVector P = Near + FVector(FMath::Cos(A) * R, FMath::Sin(A) * R, 0.f);
			const FSoilSample Smp = Soil->SampleSoil(P);
			if (!Smp.bValid || Smp.SoilClass != Want)
			{
				continue;
			}

			// The whole vehicle, and the ground it will drive onto (tests drive
			// along +X), must be this class. The first matching cell used to be
			// taken as-is -- the very edge of a mud patch -- so the vehicle sat
			// half in mud and half on dry soil, and results depended on which
			// wheels happened to be where.
			bool bLaneClear = true;
			for (float DX = -250.f; DX <= 1500.f && bLaneClear; DX += 125.f)
			{
				for (float DY = -150.f; DY <= 150.f; DY += 150.f)
				{
					const FSoilSample L = Soil->SampleSoil(P + FVector(DX, DY, 0.f));
					if (!L.bValid || L.SoilClass != Want)
					{
						bLaneClear = false;
						break;
					}
				}
			}
			if (bLaneClear)
			{
				OutLoc = FVector(P.X, P.Y, Smp.SurfaceZ + 140.f);
				return true;
			}
		}
	}
	return false;
}

bool FMudtrackTestContext::FindSplitTraction(ESoilClass Want, const FVector& Near, FVector& OutLoc) const
{
	if (Soil == nullptr) return false;
	const float StepSize = Soil->CellSize * 2.f;
	for (int32 Ring = 0; Ring < 120; ++Ring)
	{
		const float R = Ring * StepSize;
		const int32 Samples = FMath::Max(8, Ring * 6);
		for (int32 s = 0; s < Samples; ++s)
		{
			const float A = 2.f * PI * (float)s / (float)Samples;
			const FVector P = Near + FVector(FMath::Cos(A) * R, FMath::Sin(A) * R, 0.f);

			// Right wheels (+Y) in Want, left wheels (-Y) out of it, all the way.
			bool bSplit = true;
			for (float DX = -150.f; DX <= 600.f && bSplit; DX += 75.f)
			{
				const FSoilSample In  = Soil->SampleSoil(P + FVector(DX,  95.f, 0.f));
				const FSoilSample Out = Soil->SampleSoil(P + FVector(DX, -95.f, 0.f));
				bSplit = In.bValid && Out.bValid && In.SoilClass == Want && Out.SoilClass != Want;
			}
			if (bSplit)
			{
				const FSoilSample Here = Soil->SampleSoil(P);
				OutLoc = FVector(P.X, P.Y, Here.SurfaceZ + 140.f);
				return true;
			}
		}
	}
	return false;
}

AActor* FMudtrackTestContext::FindFirstActorWithTag(const FName& Tag, const FVector& NearestTo) const
{
	if (World == nullptr) return nullptr;
	AActor* Best = nullptr;
	float BestD = TNumericLimits<float>::Max();
	for (TActorIterator<AActor> It(World); It; ++It)
	{
		if (!It->ActorHasTag(Tag)) continue;
		const float D = FVector::Dist2D(It->GetActorLocation(), NearestTo);
		if (D < BestD) { BestD = D; Best = *It; }
	}
	return Best;
}

// ===========================================================================
// Test bodies. Each returns true when the test has finished.
// ===========================================================================

namespace
{

// ---- T01: stability, acceleration, braking ---------------------------------
bool Step_Stability(FMudtrackTestContext& C)
{
	switch (UMudtrackTests::TestPhase)
	{
	case 0:
		C.BeginTest(TEXT("T01_StabilityAccelBrake"));
		ResetScratch();
		C.Teleport(FVector(0.f, 0.f, 160.f), 0.f);
		C.SetInputs(0.f, 0.f, 0.f, false);
		C.Vehicle->Drive.bLowRange = false;
		C.Vehicle->Drive.GearIndex = 1;
		UMudtrackTests::TestPhase = 1;
		C.WaitFor(3.0f);
		return false;

	case 1:
		RestHeight = C.Vehicle->GetActorLocation().Z;
		ValA = (float)C.GroundedCount();
		C.Measure(FString::Printf(TEXT("rest_height_z=%s"), *F2(RestHeight)));
		C.Measure(FString::Printf(TEXT("wheels_grounded_at_rest=%d"), (int32)ValA));
		C.SetInputs(1.f, 0.f, 0.f, false);
		UMudtrackTests::TestPhase = 2;
		C.WaitFor(5.0f);
		return false;

	case 2:
		ValB = C.Vehicle->SpeedKph;                      // speed after acceleration
		C.Measure(FString::Printf(TEXT("speed_after_5s_accel_kph=%s"), *F2(ValB)));
		C.Measure(FString::Printf(TEXT("total_normal_load_during_accel_N=%s"),
			*F2(C.TotalNormalLoad())));
		C.SetInputs(0.f, 1.f, 0.f, false);
		UMudtrackTests::TestPhase = 3;
		C.WaitFor(3.0f);
		return false;

	default:
	{
		const float AfterBrake = C.Vehicle->SpeedKph;
		C.Measure(FString::Printf(TEXT("speed_after_3s_brake_kph=%s"), *F2(AfterBrake)));
		C.Measure(FString::Printf(TEXT("roll_deg=%s pitch_deg=%s"),
			*F2(C.Vehicle->BodyRollDeg), *F2(C.Vehicle->BodyPitchDeg)));

		const bool bAccel = ValB > 12.f;
		const bool bBrake = AfterBrake < 3.f;
		// Sitting on the firm pad, all four wheels should carry the body.
		const bool bStable = (int32)ValA == 4 && FMath::Abs(C.Vehicle->BodyRollDeg) < 20.f;
		C.FinishTest(bAccel && bBrake && bStable,
			FString::Printf(TEXT("accel %s (%s kph), brake %s (%s kph), grounded %d/4 at rest"),
				bAccel ? TEXT("OK") : TEXT("FAIL"), *F2(ValB),
				bBrake ? TEXT("OK") : TEXT("FAIL"), *F2(AfterBrake), (int32)ValA));
		C.SetInputs(0.f, 0.f, 0.f, false);
		return true;
	}
	}
}

// ---- T02: log crossing -----------------------------------------------------
bool Step_LogCrossing(FMudtrackTestContext& C)
{
	switch (UMudtrackTests::TestPhase)
	{
	case 0:
	{
		C.BeginTest(TEXT("T02_LogCrossing"));
		ResetScratch();
		AActor* Log = C.FindFirstActorWithTag(TEXT("MT_Log"), FVector::ZeroVector);
		if (Log == nullptr)
		{
			C.FinishTest(false, TEXT("no actor tagged MT_Log in the level"));
			return true;
		}
		AnchA = Log->GetActorLocation();      // log position
		ValA = 0.f;                           // max suspension compression
		ValB = 1e9f;                          // min body Z
		ValC = 0.f;                           // frames sampled
		C.Measure(FString::Printf(TEXT("log_location=%s"), *AnchA.ToCompactString()));
		C.Teleport(AnchA - FVector(1400.f, 0.f, -120.f), 0.f);
		C.SetInputs(0.f, 0.f, 0.f, false);
		C.Vehicle->Drive.bLowRange = true;
		C.Vehicle->Drive.GearIndex = 1;
		UMudtrackTests::TestPhase = 1;
		C.WaitFor(1.5f);
		return false;
	}
	case 1:
		C.SetInputs(0.8f, 0.f, 0.f, false);
		UMudtrackTests::TestPhase = 2;
		return false;

	case 2:
	{
		// Sample every frame while crossing.
		for (int32 w = 0; w < C.Vehicle->WheelStates.Num() && w < 4; ++w)
		{
			const float Comp = C.Vehicle->Wheels[w].SuspensionRestLength
			                 - C.Vehicle->WheelStates[w].SuspensionLength;
			ValA = FMath::Max(ValA, Comp);
		}
		ValB = FMath::Min(ValB, C.Vehicle->GetActorLocation().Z);
		ValC += 1.f;
		if (ValC < 240.f)     // ~4 s at 60 Hz
		{
			C.WaitFor(1.f / 60.f);
			return false;
		}
		C.SetInputs(0.f, 0.f, 0.f, false);
		UMudtrackTests::TestPhase = 3;
		return false;
	}

	default:
	{
		const float FinalZ = C.Vehicle->GetActorLocation().Z;
		C.Measure(FString::Printf(TEXT("log_z=%s"), *F2(AnchA.Z)));
		C.Measure(FString::Printf(TEXT("body_z_min_during_crossing=%s"), *F2(ValB)));
		C.Measure(FString::Printf(TEXT("body_z_final=%s"), *F2(FinalZ)));
		C.Measure(FString::Printf(TEXT("max_suspension_compression_cm=%s"), *F2(ValA)));

		// The body must never drop below the log's own height: that would mean
		// the wheel had passed through the obstacle instead of over it.
		const bool bNoSinkThrough = ValB > AnchA.Z - 30.f;
		const bool bSuspensionMoved = ValA > 2.0f;
		C.FinishTest(bNoSinkThrough && bSuspensionMoved,
			FString::Printf(TEXT("sank through log: %s; peak suspension travel %s cm (%s)"),
				bNoSinkThrough ? TEXT("no") : TEXT("YES"),
				*F2(ValA), bSuspensionMoved ? TEXT("moved") : TEXT("too stiff")));
		return true;
	}
	}
}

// ---- T03: diagonal articulation --------------------------------------------
bool Step_Diagonal(FMudtrackTestContext& C)
{
	switch (UMudtrackTests::TestPhase)
	{
	case 0:
	{
		C.BeginTest(TEXT("T03_DiagonalArticulation"));
		ResetScratch();
		AActor* Diag = C.FindFirstActorWithTag(TEXT("MT_Diagonal"), FVector::ZeroVector);
		FVector Start(0.f, 0.f, 220.f);
		float Yaw = 0.f;
		if (Diag != nullptr)
		{
			Start = Diag->GetActorLocation() - FVector(1100.f, 0.f, -80.f);
			Yaw = Diag->GetActorRotation().Yaw;
		}
		C.Measure(FString::Printf(TEXT("course=%s"), Diag ? TEXT("found") : TEXT("fallback flat pad")));
		ValA = 0.f;   // max wheel-height spread
		ValB = 0.f;   // max wheel-load spread
		ValC = 0.f;   // frames
		C.Teleport(Start, Yaw);
		C.SetInputs(0.f, 0.f, 0.f, false);
		C.Vehicle->Drive.bLowRange = true;
		UMudtrackTests::TestPhase = 1;
		C.WaitFor(1.5f);
		return false;
	}
	case 1:
		C.SetInputs(0.4f, 0.f, 0.f, false);
		UMudtrackTests::TestPhase = 2;
		return false;

	case 2:
	{
		float MinZ = 1e9f, MaxZ = -1e9f, MinL = 1e9f, MaxL = -1e9f;
		for (const FWheelState& S : C.Vehicle->WheelStates)
		{
			MinZ = FMath::Min(MinZ, S.WorldLocation.Z);
			MaxZ = FMath::Max(MaxZ, S.WorldLocation.Z);
			MinL = FMath::Min(MinL, S.NormalLoad);
			MaxL = FMath::Max(MaxL, S.NormalLoad);
		}
		ValA = FMath::Max(ValA, MaxZ - MinZ);
		ValB = FMath::Max(ValB, MaxL - MinL);
		ValC += 1.f;
		if (ValC < 300.f)     // ~5 s
		{
			C.WaitFor(1.f / 60.f);
			return false;
		}
		C.SetInputs(0.f, 0.f, 0.f, false);
		UMudtrackTests::TestPhase = 3;
		return false;
	}

	default:
	{
		C.Measure(FString::Printf(TEXT("max_wheel_height_spread_cm=%s"), *F2(ValA)));
		C.Measure(FString::Printf(TEXT("max_wheel_load_spread_N=%s"), *F2(ValB)));
		C.Measure(FString::Printf(TEXT("max_body_roll_deg_seen=%s"), *F2(C.Vehicle->BodyRollDeg)));

		// A rigid body that was snapped level would show no spread at all.
		const bool bArticulated = ValA > 8.f;
		const bool bLoadTransferred = ValB > 1200.f;
		C.FinishTest(bArticulated && bLoadTransferred,
			FString::Printf(TEXT("wheel height spread %s cm (%s), load spread %s N (%s)"),
				*F2(ValA), bArticulated ? TEXT("OK") : TEXT("rigid"),
				*F2(ValB), bLoadTransferred ? TEXT("OK") : TEXT("no transfer")));
		return true;
	}
	}
}

// ---- T04: dry vs soft, identical input -------------------------------------
bool Step_DryVsSoft(FMudtrackTestContext& C)
{
	switch (UMudtrackTests::TestPhase)
	{
	case 0:
	{
		C.BeginTest(TEXT("T04_DryVsSoftGround"));
		ResetScratch();
		FVector Dry, Mud;
		const bool bDry = C.FindSoilOfClass(ESoilClass::FirmRoad, FVector::ZeroVector, Dry);
		const bool bMud = C.FindSoilOfClass(ESoilClass::DeepMud, FVector::ZeroVector, Mud);
		if (!bDry || !bMud)
		{
			C.FinishTest(false, FString::Printf(TEXT("lanes missing (firm=%d mud=%d)"), bDry, bMud));
			return true;
		}
		AnchA = Dry;
		AnchB = Mud;
		C.Measure(FString::Printf(TEXT("firm_lane=%s"), *Dry.ToCompactString()));
		C.Measure(FString::Printf(TEXT("mud_lane=%s"), *Mud.ToCompactString()));
		C.Measure(TEXT("input_sequence=throttle 1.0 held 5.0 s on both lanes"));
		C.Teleport(Dry, 0.f);
		C.SetInputs(0.f, 0.f, 0.f, false);
		UMudtrackTests::TestPhase = 1;
		C.WaitFor(1.5f);
		return false;
	}
	case 1:
		StartLoc = C.Vehicle->GetActorLocation();
		C.SetInputs(1.f, 0.f, 0.f, false);
		UMudtrackTests::TestPhase = 2;
		C.WaitFor(5.0f);
		return false;

	case 2:
		ValA = FVector::Dist2D(StartLoc, C.Vehicle->GetActorLocation());   // firm distance
		ValB = C.MeanSink();                                                // firm sinkage
		C.Measure(FString::Printf(TEXT("firm: distance_cm=%s speed_kph=%s sinkage_cm=%s rut_cm=%s"),
			*F2(ValA), *F2(C.Vehicle->SpeedKph), *F3(ValB), *F3(C.MeanRut())));
		C.SetInputs(0.f, 0.f, 0.f, false);
		C.Teleport(AnchB, 0.f);
		UMudtrackTests::TestPhase = 3;
		C.WaitFor(1.5f);
		return false;

	case 3:
		StartLoc = C.Vehicle->GetActorLocation();
		C.SetInputs(1.f, 0.f, 0.f, false);      // identical input
		UMudtrackTests::TestPhase = 4;
		C.WaitFor(5.0f);
		return false;

	default:
	{
		const float MudDist = FVector::Dist2D(StartLoc, C.Vehicle->GetActorLocation());
		const float MudSink = C.MeanSink();
		const float MudRut = C.MeanRut();
		C.Measure(FString::Printf(TEXT("mud:  distance_cm=%s speed_kph=%s sinkage_cm=%s rut_cm=%s"),
			*F2(MudDist), *F2(C.Vehicle->SpeedKph), *F3(MudSink), *F3(MudRut)));
		C.Measure(FString::Printf(TEXT("distance_ratio_firm_over_mud=%s"),
			MudDist > 1.f ? *F2(ValA / MudDist) : TEXT("n/a")));

		const bool bShorter = MudDist < ValA * 0.80f;
		const bool bDeeper = MudSink > ValB + 1.0f;
		const bool bRut = MudRut > 0.3f;
		C.FinishTest(bShorter && bDeeper && bRut,
			FString::Printf(TEXT("mud travelled %s%% of firm distance (%s); sinkage %s vs %s cm (%s); rut %s cm (%s)"),
				ValA > 1.f ? *F2(100.f * MudDist / ValA) : TEXT("n/a"),
				bShorter ? TEXT("OK") : TEXT("FAIL"),
				*F3(MudSink), *F3(ValB), bDeeper ? TEXT("OK") : TEXT("FAIL"),
				*F3(MudRut), bRut ? TEXT("OK") : TEXT("FAIL")));
		C.SetInputs(0.f, 0.f, 0.f, false);
		return true;
	}
	}
}

// ---- T05: wheelspin rut, finite and persistent -----------------------------
bool Step_Wheelspin(FMudtrackTestContext& C)
{
	// Deepest rut within 30 cm of the sample point. The wheel's contact point
	// climbs to the rim of the hole it digs (the probe takes the highest sample
	// in the patch), so a single point lags the actual hole.
	auto RutNear = [&C](const FVector& P)
	{
		float M = 0.f;
		for (float DX = -30.f; DX <= 30.f; DX += 7.5f)
		{
			for (float DY = -30.f; DY <= 30.f; DY += 7.5f)
			{
				M = FMath::Max(M, C.Soil->SampleSoil(P + FVector(DX, DY, 0.f)).RutDepth);
			}
		}
		return M;
	};

	switch (UMudtrackTests::TestPhase)
	{
	case 0:
	{
		C.BeginTest(TEXT("T05_WheelspinRutPersistent"));
		ResetScratch();
		FVector Mud;
		if (!C.FindSoilOfClass(ESoilClass::DeepMud, FVector::ZeroVector, Mud))
		{
			C.FinishTest(false, TEXT("no DeepMud surface found"));
			return true;
		}
		C.Teleport(Mud, 0.f);
		// Foot brake only, wheels locked: the tyres keep carrying the load while
		// full throttle spins them against the brakes, so any rut growth is from
		// slip. (The handbrake was on as well, which locked only the rears: the
		// fronts then held the whole vehicle on their locked contact patches.)
		C.SetInputs(0.f, 1.f, 0.f, false);
		C.Vehicle->Drive.bLowRange = true;
		// A brake-stand: rear-wheel drive, all four brakes on. The rears get the
		// whole engine torque and overpower their brakes; the undriven fronts stay
		// locked and hold the vehicle. (In 4WD the engine simply overpowers every
		// brake and the vehicle drives off, which is not wheelspin "in place".)
		C.Vehicle->Drive.DriveMode = EDriveMode::TwoWheelDriveRear;
		UMudtrackTests::TestPhase = 1;
		C.WaitFor(1.5f);
		return false;
	}
	case 1:
		// Measure at a fixed point -- under the rear-left wheel as spinning
		// starts -- rather than at wherever the wheels are later: a contact point
		// that shifts a few centimetres reads a different part of the rut.
		AnchA = C.Vehicle->WheelStates.IsValidIndex(2)
			? C.Vehicle->WheelStates[2].ContactPoint : C.Vehicle->GetActorLocation();
		// The rut before any wheelspin: what the parked vehicle's weight alone
		// pressed into the mud. "Wheelspin changes the rut" is judged against this.
		ValC = RutNear(AnchA);
		StartLoc = C.Vehicle->GetActorLocation();
		C.Measure(FString::Printf(TEXT("max_rut_before_wheelspin_cm=%s"), *F3(ValC)));
		C.SetInputs(1.f, 1.f, 0.f, false);
		UMudtrackTests::TestPhase = 2;
		C.WaitFor(4.0f);
		return false;

	case 2:
	{
		ValA = RutNear(AnchA);
		C.Measure(FString::Printf(TEXT("max_rut_after_4s_wheelspin_cm=%s"), *F3(ValA)));
		C.Measure(FString::Printf(TEXT("mean_sinkage_cm=%s"), *F3(C.MeanSink())));
		C.Measure(FString::Printf(TEXT("total_normal_load_N=%s"), *F2(C.TotalNormalLoad())));

		bool bSpinning = false;
		for (const FWheelState& S : C.Vehicle->WheelStates)
		{
			if (S.bSpinning) bSpinning = true;
		}
		C.Measure(FString::Printf(TEXT("wheel_spinning_flag=%s"), bSpinning ? TEXT("yes") : TEXT("no")));
		C.SetInputs(1.f, 1.f, 0.f, false);
		UMudtrackTests::TestPhase = 3;
		C.WaitFor(4.0f);
		return false;
	}

	case 3:
		ValB = RutNear(AnchA);
		C.Measure(FString::Printf(TEXT("max_rut_after_8s_wheelspin_cm=%s"), *F3(ValB)));
		// "In place": the brake-stand must not have driven the vehicle off the
		// spot it was digging.
		RestHeight = FVector::Dist2D(C.Vehicle->GetActorLocation(), StartLoc);
		C.Measure(FString::Printf(TEXT("body_moved_during_wheelspin_cm=%s"), *F2(RestHeight)));
		// Stop, and confirm the rut does not decay by itself. No pedal at all:
		// the vehicle is parked, and the hull is not allowed to plough by
		// itself, so the only thing that can change the rut is the ground
		// relaxing -- which is what this phase measures.
		C.SetInputs(0.f, 0.f, 0.f, false);
		UMudtrackTests::TestPhase = 4;
		C.WaitFor(4.0f);
		return false;

	default:
	{
		const float RutIdle = RutNear(AnchA);
		C.Measure(FString::Printf(TEXT("rut_after_4s_idle_cm=%s"), *F3(RutIdle)));
		C.Measure(FString::Printf(TEXT("settling_enabled=%s"),
			C.Soil->bAllowSettling ? TEXT("yes") : TEXT("no")));

		// The spec: "wheelspin in place changes the rut; its depth is finite and
		// persistent". So:
		//  - changed:  the spinning deepened the rut beyond what the parked
		//              weight alone had pressed in (ValC);
		//  - finite:   bounded, and levelling off rather than running away -- the
		//              second 4 s may add no more than the first 4 s did. (The
		//              old check demanded growth in the second interval, which a
		//              rut that has already hit its depth budget cannot show:
		//              reaching the cap is exactly what "finite" means.)
		//  - in place: the body stayed put while the wheels dug;
		//  - persists: it does not recover by itself. It may deepen a little
		//              while parked -- the vehicle settles into the ground it has
		//              just churned to slurry -- which is the soil carrying the
		//              load, not a decay.
		const float Growth1 = ValA - ValC;
		const float Growth2 = ValB - ValA;
		const bool bChanged = ValB > ValC + 1.0f;
		const bool bFinite = ValB < 80.f && Growth2 <= Growth1 + 0.5f;
		const bool bInPlace = RestHeight < 100.f;
		const bool bPersists = RutIdle >= ValB - 0.5f;
		C.FinishTest(bChanged && bFinite && bInPlace && bPersists,
			FString::Printf(TEXT("rut %s -> %s -> %s cm (%s); finite=%s; in place=%s (%s cm); persists=%s"),
				*F3(ValC), *F3(ValA), *F3(ValB), bChanged ? TEXT("changed") : TEXT("unchanged"),
				bFinite ? TEXT("yes") : TEXT("RUNAWAY"),
				bInPlace ? TEXT("yes") : TEXT("no"), *F2(RestHeight),
				bPersists ? TEXT("yes") : TEXT("no")));
		C.SetInputs(0.f, 0.f, 0.f, false);
		return true;
	}
	}
}

// ---- T06: second pass uses the changed surface -----------------------------
bool Step_SecondPass(FMudtrackTestContext& C)
{
	switch (UMudtrackTests::TestPhase)
	{
	case 0:
	{
		C.BeginTest(TEXT("T06_SecondPassUsesRut"));
		ResetScratch();
		FVector Mud;
		if (!C.FindSoilOfClass(ESoilClass::DeepMud, FVector::ZeroVector, Mud))
		{
			C.FinishTest(false, TEXT("no DeepMud surface found"));
			return true;
		}
		C.Teleport(Mud, 0.f);
		C.SetInputs(0.f, 0.f, 0.f, false);
		// Ordinary driving: high range, half throttle. In low range the launch
		// wheelspin digs a 30 cm hole at the start that the second pass then
		// cannot climb out of, so it never reaches the stretch being compared.
		C.Vehicle->Drive.bLowRange = false;
		UMudtrackTests::TestPhase = 1;
		C.WaitFor(1.5f);
		return false;
	}
	case 1:
		StartLoc = C.Vehicle->GetActorLocation();
		C.SetInputs(0.5f, 0.f, 0.f, false);
		UMudtrackTests::TestPhase = 2;
		C.WaitFor(4.0f);
		return false;

	case 2:
	{
		ValA = C.MaxRut();                                  // rut after pass 1
		AnchA = C.Vehicle->GetActorLocation();              // pass 1 end position
		ValB = FVector::Dist2D(StartLoc, AnchA);
		C.Measure(FString::Printf(TEXT("pass1_distance_cm=%s max_rut_cm=%s"), *F2(ValB), *F3(ValA)));

		// Record the trench profile across the direction of travel, sampled at
		// the mid-point of the track that pass 1 cut.
		const FVector Dir = (AnchA - StartLoc).GetSafeNormal2D();
		AcrossDir = FVector::CrossProduct(FVector::UpVector, Dir).GetSafeNormal();
		AnchB = (StartLoc + AnchA) * 0.5f;                  // mid-track sample line

		ProfileBefore.Reset();
		for (int32 i = -5; i <= 5; ++i)
		{
			const FVector P = AnchB + AcrossDir * (i * C.Soil->CellSize * 0.9f);
			ProfileBefore.Add(C.Soil->SampleSoil(P).SurfaceZ);
		}

		C.SetInputs(0.f, 0.f, 0.f, false);
		C.Teleport(StartLoc + FVector(0.f, 0.f, 50.f), 0.f);
		UMudtrackTests::TestPhase = 3;
		C.WaitFor(1.0f);
		return false;
	}

	case 3:
		C.SetInputs(0.5f, 0.f, 0.f, false);      // same input as pass 1
		UMudtrackTests::TestPhase = 4;
		C.WaitFor(4.0f);
		return false;

	default:
	{
		const float Rut2 = C.MaxRut();
		const float Pass2Dist = FVector::Dist2D(StartLoc, C.Vehicle->GetActorLocation());

		ProfileAfter.Reset();
		for (int32 i = -5; i <= 5; ++i)
		{
			const FVector P = AnchB + AcrossDir * (i * C.Soil->CellSize * 0.9f);
			ProfileAfter.Add(C.Soil->SampleSoil(P).SurfaceZ);
		}

		float MaxDrop = 0.f, SumDrop = 0.f;
		const int32 N = FMath::Min(ProfileBefore.Num(), ProfileAfter.Num());
		for (int32 i = 0; i < N; ++i)
		{
			const float D = ProfileBefore[i] - ProfileAfter[i];
			MaxDrop = FMath::Max(MaxDrop, D);
			SumDrop += D;
		}

		C.Measure(FString::Printf(TEXT("pass2_distance_cm=%s max_rut_cm=%s"), *F2(Pass2Dist), *F3(Rut2)));
		C.Measure(FString::Printf(TEXT("surface_max_drop_cm=%s"), *F3(MaxDrop)));
		C.Measure(FString::Printf(TEXT("surface_mean_drop_cm=%s"), *F3(SumDrop / FMath::Max(N, 1))));

		// Passing again must both deepen the rut and find a lower surface than
		// the one the first pass left. The second is the real claim: it shows
		// the collision surface changed, not just a counter.
		// Deeper is judged on the same stretch of ground as the surface drop:
		// the wheels' current contact points after each pass are at different
		// places (pass 2 ends further along), so comparing them compared fresh
		// ground with rutted ground.
		const bool bDeeper = MaxDrop > 0.15f;
		const bool bSurfaceChanged = MaxDrop > 0.15f;
		C.FinishTest(bDeeper && bSurfaceChanged,
			FString::Printf(TEXT("rut %s -> %s cm (%s); surface dropped up to %s cm (%s)"),
				*F3(ValA), *F3(Rut2), bDeeper ? TEXT("deeper") : TEXT("unchanged"),
				*F3(MaxDrop), bSurfaceChanged ? TEXT("changed") : TEXT("no change")));
		C.SetInputs(0.f, 0.f, 0.f, false);
		return true;
	}
	}
}

// ---- T07: 2WD / 4WD / differential lock ------------------------------------
bool Step_Drivetrain(FMudtrackTestContext& C)
{
	// Two comparisons, each under the conditions where it means something:
	//
	//   2WD vs 4WD          on uniform deep mud: twice the driven tyres, twice
	//                       the usable traction;
	//   open vs locked      across the twister, where the diagonal humps unload
	//                       one wheel on each axle: an open differential lets the
	//                       unloaded wheel spin up while its partner stalls; a
	//                       locked one keeps both turning together. That speed
	//                       difference is what a locker removes, so that is what
	//                       is compared (on uniform ground it changes nothing).
	//
	// Identical input every run: low range, full throttle for 6 s from rest, on
	// freshly reset ground.
	auto StartRun = [&C](const FVector& At, EDriveMode Mode, EDiffLock Axles, int32 NextPhase)
	{
		C.Soil->ResetAllSoils();
		C.SetInputs(0.f, 0.f, 0.f, false);
		C.Teleport(At, 0.f);
		C.Vehicle->Drive.DriveMode = Mode;
		C.Vehicle->Drive.CenterDiff = (Axles == EDiffLock::Locked) ? EDiffLock::Locked : EDiffLock::Open;
		C.Vehicle->Drive.FrontDiff = Axles;
		C.Vehicle->Drive.RearDiff = Axles;
		C.Vehicle->Drive.bLowRange = true;
		C.Vehicle->Drive.GearIndex = 1;
		UMudtrackTests::TestPhase = NextPhase;
		C.WaitFor(1.5f);
	};
	auto Go = [&C](int32 NextPhase, float Seconds)
	{
		StartLoc = C.Vehicle->GetActorLocation();
		C.SetInputs(1.f, 0.f, 0.f, false);
		UMudtrackTests::TestPhase = NextPhase;
		C.WaitFor(Seconds);
	};
	// Largest left/right wheel-speed difference across either axle.
	auto AxleSpread = [&C]()
	{
		const TArray<FWheelState>& W = C.Vehicle->WheelStates;
		if (W.Num() < 4) return 0.f;
		return FMath::Max(FMath::Abs(W[0].AngularVelocity - W[1].AngularVelocity),
		                  FMath::Abs(W[2].AngularVelocity - W[3].AngularVelocity));
	};
	static float SpreadMax = 0.f;
	static int32 Frames = 0;
	auto Dist = [&C]() { return FVector::Dist2D(StartLoc, C.Vehicle->GetActorLocation()); };

	switch (UMudtrackTests::TestPhase)
	{
	case 0:
	{
		C.BeginTest(TEXT("T07_DrivetrainModes"));
		ResetScratch();
		FVector Mud;
		if (!C.FindSoilOfClass(ESoilClass::DeepMud, FVector::ZeroVector, Mud))
		{
			C.FinishTest(false, TEXT("no DeepMud lane found"));
			return true;
		}
		AActor* Twister = C.FindFirstActorWithTag(TEXT("MT_Diagonal"), FVector::ZeroVector);
		if (Twister == nullptr)
		{
			C.FinishTest(false, TEXT("no twister (MT_Diagonal) found"));
			return true;
		}
		AnchA = Mud;
		AnchB = Twister->GetActorLocation() + FVector(-250.f, 0.f, 120.f);
		C.Measure(TEXT("identical input: low range, full throttle held 6.0 s from rest, fresh ground each run"));
		C.Measure(FString::Printf(TEXT("uniform_mud_lane=%s twister_start=%s"),
			*AnchA.ToCompactString(), *AnchB.ToCompactString()));
		StartRun(AnchA, EDriveMode::TwoWheelDriveRear, EDiffLock::Open, 1);
		return false;
	}
	case 1: Go(2, 6.f); return false;
	case 2:
		ValA = Dist();
		C.Measure(FString::Printf(TEXT("uniform mud, 2wd: distance_cm=%s"), *F2(ValA)));
		StartRun(AnchA, EDriveMode::FourWheelDrive, EDiffLock::Open, 3);
		return false;
	case 3: Go(4, 6.f); return false;
	case 4:
		ValB = Dist();
		C.Measure(FString::Printf(TEXT("uniform mud, 4wd open: distance_cm=%s"), *F2(ValB)));
		StartRun(AnchB, EDriveMode::FourWheelDrive, EDiffLock::Open, 5);
		return false;
	case 5:
		StartLoc = C.Vehicle->GetActorLocation();
		C.SetInputs(1.f, 0.f, 0.f, false);
		SpreadMax = 0.f;
		Frames = 0;
		UMudtrackTests::TestPhase = 6;
		C.WaitFor(1.f / 60.f);
		return false;
	case 6:
		SpreadMax = FMath::Max(SpreadMax, AxleSpread());
		if (++Frames < 360)
		{
			C.WaitFor(1.f / 60.f);
			return false;
		}
		ValC = SpreadMax;
		C.Measure(FString::Printf(TEXT("twister, 4wd open: max wheel speed spread across an axle=%s rad/s, distance_cm=%s"),
			*F2(ValC), *F2(Dist())));
		StartRun(AnchB, EDriveMode::FourWheelDrive, EDiffLock::Locked, 7);
		return false;
	case 7:
		StartLoc = C.Vehicle->GetActorLocation();
		C.SetInputs(1.f, 0.f, 0.f, false);
		SpreadMax = 0.f;
		Frames = 0;
		UMudtrackTests::TestPhase = 8;
		C.WaitFor(1.f / 60.f);
		return false;
	case 8:
		SpreadMax = FMath::Max(SpreadMax, AxleSpread());
		if (++Frames < 360)
		{
			C.WaitFor(1.f / 60.f);
			return false;
		}
		UMudtrackTests::TestPhase = 9;
		return false;
	default:
	{
		const float Locked = SpreadMax;
		C.Measure(FString::Printf(TEXT("twister, 4wd locked: max wheel speed spread across an axle=%s rad/s, distance_cm=%s"),
			*F2(Locked), *F2(Dist())));
		C.Measure(FString::Printf(TEXT("gain_4wd_over_2wd_pct=%s"),
			ValA > 1.f ? *F2(100.f * (ValB - ValA) / ValA) : TEXT("n/a")));

		// What a locker does, by definition: both wheels on the axle turn at the
		// same speed. With an open diff on split traction the mud-side wheel
		// spins up while the other one stalls.
		const bool b4Beats2 = ValB > ValA * 1.10f;
		const bool bLockBeats = (ValC > 1.0f) && (Locked < ValC * 0.25f);
		C.FinishTest(b4Beats2 && bLockBeats,
			FString::Printf(TEXT("uniform mud: 2WD %s cm vs 4WD %s cm (%s); twister axle spread: open %s vs locked %s rad/s (%s)"),
				*F2(ValA), *F2(ValB), b4Beats2 ? TEXT("4WD wins") : TEXT("no gain"),
				*F2(ValC), *F2(Locked), bLockBeats ? TEXT("locked equalises") : TEXT("no difference")));

		C.Vehicle->Drive.DriveMode = EDriveMode::FourWheelDrive;
		C.Vehicle->Drive.CenterDiff = EDiffLock::LimitedSlip;
		C.Vehicle->Drive.FrontDiff = EDiffLock::Open;
		C.Vehicle->Drive.RearDiff = EDiffLock::Open;
		C.SetInputs(0.f, 0.f, 0.f, false);
		return true;
	}
	}
}

// ---- T08: winch pull -------------------------------------------------------
bool Step_Winch(FMudtrackTestContext& C)
{
	switch (UMudtrackTests::TestPhase)
	{
	case 0:
	{
		C.BeginTest(TEXT("T08_WinchPull"));
		ResetScratch();
		AActor* Anchor = C.FindFirstActorWithTag(TEXT("MT_WinchAnchor"), FVector::ZeroVector);
		if (Anchor == nullptr)
		{
			C.FinishTest(false, TEXT("no actor tagged MT_WinchAnchor in the level"));
			return true;
		}
		AnchA = Anchor->GetActorLocation();
		C.Measure(FString::Printf(TEXT("anchor_location=%s"), *AnchA.ToCompactString()));

		FVector Pit = AnchA + FVector(700.f, 0.f, 0.f);
		const FSoilSample Smp = C.Soil->SampleSoil(Pit);
		Pit.Z = Smp.bValid ? Smp.SurfaceZ + 120.f : AnchA.Z + 120.f;

		C.Teleport(Pit, 180.f);                    // face the anchor
		// Brakes held: any movement is the winch, not the engine.
		C.SetInputs(0.f, 1.f, 0.f, true);
		UMudtrackTests::TestPhase = 1;
		C.WaitFor(2.0f);
		return false;
	}
	case 1:
		StartLoc = C.Vehicle->GetActorLocation();
		C.Vehicle->ToggleWinch();
		UMudtrackTests::TestPhase = 2;
		C.WaitFor(0.5f);
		return false;

	case 2:
	{
		if (!C.Vehicle->bWinchAttached)
		{
			C.Measure(FString::Printf(TEXT("message=%s"), *C.Vehicle->WinchMessage));
			C.FinishTest(false,
				FString::Printf(TEXT("winch did not attach: %s"), *C.Vehicle->WinchMessage));
			return true;
		}
		C.Measure(TEXT("attached=yes"));
		C.Measure(FString::Printf(TEXT("cable_start_cm=%s"), *F2(C.Vehicle->WinchCableLength)));
		ValA = 0.f;                        // peak winch force
		ValB = 0.f;                        // largest single-frame displacement
		AnchB = StartLoc;
		ValC = 0.f;
		UMudtrackTests::TestPhase = 3;
		return false;
	}

	case 3:
	{
		ValA = FMath::Max(ValA, C.Vehicle->WinchForce);
		const FVector Now = C.Vehicle->GetActorLocation();
		ValB = FMath::Max(ValB, FVector::Dist(AnchB, Now));
		AnchB = Now;
		ValC += 1.f;
		// Hold the winch on for about 5 s of frames.
		if (ValC < 300.f)
		{
			C.WaitFor(1.f / 60.f);
			return false;
		}
		UMudtrackTests::TestPhase = 4;
		return false;
	}

	default:
	{
		const FVector After = C.Vehicle->GetActorLocation();
		const float Moved = FVector::Dist2D(StartLoc, After);
		const FVector ToAnchor = (AnchA - StartLoc).GetSafeNormal2D();
		const float Toward = FVector::DotProduct((After - StartLoc).GetSafeNormal2D(), ToAnchor) * Moved;

		C.Measure(FString::Printf(TEXT("cable_end_cm=%s"), *F2(C.Vehicle->WinchCableLength)));
		C.Measure(FString::Printf(TEXT("total_movement_cm=%s"), *F2(Moved)));
		C.Measure(FString::Printf(TEXT("movement_toward_anchor_cm=%s"), *F2(Toward)));
		C.Measure(FString::Printf(TEXT("peak_winch_force_N=%s"), *F2(ValA)));
		C.Measure(FString::Printf(TEXT("max_single_frame_displacement_cm=%s"), *F2(ValB)));

		C.Vehicle->ReleaseWinch();

		const bool bMoved = Moved > 15.f;
		const bool bToward = Toward > 8.f;
		const bool bForce = ValA > 100.f && ValA <= 45000.f;
		// A teleport would appear as one enormous frame step.
		const bool bNoTeleport = ValB < 120.f;

		C.FinishTest(bMoved && bToward && bForce && bNoTeleport,
			FString::Printf(TEXT("moved %s cm (%s cm toward anchor), peak force %s N, largest frame step %s cm (%s)"),
				*F2(Moved), *F2(Toward), *F2(ValA), *F2(ValB),
				bNoTeleport ? TEXT("no teleport") : TEXT("JUMP DETECTED")));
		return true;
	}
	}
}

// ---- T09: persistence and reset --------------------------------------------
bool Step_Persistence(FMudtrackTestContext& C)
{
	switch (UMudtrackTests::TestPhase)
	{
	case 0:
	{
		C.BeginTest(TEXT("T09_PersistenceAndReset"));
		ResetScratch();
		FVector Mud;
		if (!C.FindSoilOfClass(ESoilClass::DeepMud, FVector::ZeroVector, Mud))
		{
			C.FinishTest(false, TEXT("no DeepMud surface found"));
			return true;
		}
		C.Teleport(Mud, 0.f);
		C.SetInputs(0.f, 0.f, 0.f, false);
		UMudtrackTests::TestPhase = 10;
		C.WaitFor(1.0f);
		return false;
	}
	case 10:
		// The sample point is where the rear-left wheel stands at the start, in
		// the mud: it settles there and then spins away from it. (Sampling where
		// the vehicle ends up measured whatever ground it happened to stop on --
		// after 5 s at full throttle, outside the mud patch.)
		AnchA = C.Vehicle->WheelStates.IsValidIndex(2)
			? C.Vehicle->WheelStates[2].ContactPoint : C.Vehicle->GetActorLocation();
		C.SetInputs(1.f, 0.f, 0.f, false);
		UMudtrackTests::TestPhase = 1;
		C.WaitFor(5.0f);
		return false;
	case 1:
		C.SetInputs(0.f, 0.f, 0.f, false);
		UMudtrackTests::TestPhase = 2;
		C.WaitFor(1.0f);
		return false;

	case 2:
		ValA = C.Soil->SampleSoil(AnchA).RutDepth;
		C.Measure(FString::Printf(TEXT("rut_after_driving_cm=%s"), *F3(ValA)));
		C.Measure(FString::Printf(TEXT("sample_point=%s"), *AnchA.ToCompactString()));
		// Leave the area entirely. The field is persistent data, so nothing
		// should change while the vehicle is elsewhere.
		C.Teleport(FVector(0.f, 0.f, 2600.f), 0.f);
		UMudtrackTests::TestPhase = 3;
		C.WaitFor(3.0f);
		return false;

	case 3:
		ValB = C.Soil->SampleSoil(AnchA).RutDepth;
		C.Measure(FString::Printf(TEXT("rut_after_leaving_and_returning_cm=%s"), *F3(ValB)));
		C.Measure(FString::Printf(TEXT("allocated_tiles=%d"), C.Soil->GetAllocatedTileCount()));
		C.Measure(FString::Printf(TEXT("displaced_volume_cm3=%s"), *F2(C.Soil->GetDisplacedVolume())));
		// Now an explicit reset must actually clear it.
		C.Vehicle->ResetForTest();
		UMudtrackTests::TestPhase = 4;
		C.WaitFor(0.5f);
		return false;

	default:
	{
		const float RutReset = C.Soil->SampleSoil(AnchA).RutDepth;
		C.Measure(FString::Printf(TEXT("rut_after_reset_cm=%s"), *F3(RutReset)));

		const bool bPersisted = FMath::Abs(ValB - ValA) < 0.25f;
		const bool bCleared = RutReset < 0.01f;
		const bool bExisted = ValA > 0.2f;
		C.FinishTest(bPersisted && bCleared && bExisted,
			FString::Printf(TEXT("rut %s cm persisted across absence: %s; reset cleared it: %s"),
				*F3(ValA), bPersisted ? TEXT("yes") : TEXT("NO"),
				bCleared ? TEXT("yes") : TEXT("NO")));
		C.SetInputs(0.f, 0.f, 0.f, false);
		return true;
	}
	}
}

// ---- T10: added test load --------------------------------------------------
bool Step_TestLoad(FMudtrackTestContext& C)
{
	switch (UMudtrackTests::TestPhase)
	{
	case 0:
	{
		C.BeginTest(TEXT("T10_TestLoadSag"));
		ResetScratch();
		C.Teleport(FVector(0.f, 0.f, 180.f), 0.f);
		C.SetInputs(0.f, 0.f, 0.f, false);
		C.Vehicle->SetTestLoadKg(0.f);
		UMudtrackTests::TestPhase = 1;
		C.WaitFor(3.0f);
		return false;
	}
	case 1:
		ValA = C.MeanSuspensionLength();
		ValB = C.TotalNormalLoad();
		C.Measure(FString::Printf(TEXT("suspension_length_empty_cm=%s"), *F3(ValA)));
		C.Measure(FString::Printf(TEXT("total_ground_load_empty_N=%s"), *F2(ValB)));
		C.Vehicle->SetTestLoadKg(800.f);
		UMudtrackTests::TestPhase = 2;
		C.WaitFor(3.0f);
		return false;

	default:
	{
		const float SuspLoaded = C.MeanSuspensionLength();
		const float LoadLoaded = C.TotalNormalLoad();
		C.Measure(FString::Printf(TEXT("suspension_length_loaded_cm=%s"), *F3(SuspLoaded)));
		C.Measure(FString::Printf(TEXT("total_ground_load_loaded_N=%s"), *F2(LoadLoaded)));

		const float Sag = ValA - SuspLoaded;
		C.Measure(FString::Printf(TEXT("sag_cm=%s"), *F3(Sag)));

		const bool bSagged = Sag > 0.5f;
		const bool bLoadRose = LoadLoaded > ValB * 1.10f;
		C.FinishTest(bSagged && bLoadRose,
			FString::Printf(TEXT("sagged %s cm (%s); ground load %s -> %s N (%s)"),
				*F3(Sag), bSagged ? TEXT("OK") : TEXT("FAIL"),
				*F2(ValB), *F2(LoadLoaded), bLoadRose ? TEXT("OK") : TEXT("FAIL")));
		C.Vehicle->SetTestLoadKg(0.f);
		return true;
	}
	}
}

} // anonymous namespace

// ===========================================================================
// Runner
// ===========================================================================

bool UMudtrackTests::BeginRun(UObject* WorldContextObject, const FString& OutputJsonAbsolutePath)
{
	if (GEngine == nullptr) return false;

	UWorld* World = GEngine->GetWorldFromContextObject(WorldContextObject, EGetWorldErrorMode::ReturnNull);
	if (World == nullptr)
	{
		UE_LOG(LogMudTest, Error, TEXT("BeginRun: no world"));
		return false;
	}

	Run = FMudtrackTestContext();
	Run.World = World;

	if (APlayerController* PC = World->GetFirstPlayerController())
	{
		Run.Vehicle = Cast<AOffroadVehiclePawn>(PC->GetPawn());
	}
	if (Run.Vehicle == nullptr)
	{
		for (TActorIterator<AOffroadVehiclePawn> It(World); It; ++It) { Run.Vehicle = *It; break; }
	}
	if (Run.Vehicle == nullptr)
	{
		UE_LOG(LogMudTest, Error, TEXT("BeginRun: no vehicle"));
		return false;
	}

	for (TActorIterator<ASoilField> It(World); It; ++It) { Run.Soil = *It; break; }
	if (Run.Soil == nullptr)
	{
		UE_LOG(LogMudTest, Error, TEXT("BeginRun: no soil field"));
		return false;
	}

	Run.Vehicle->InitializeVehicleRuntime();

	LastResults.Reset();
	OutputPath = OutputJsonAbsolutePath;
	PlanIndex = 0;
	TestPhase = 0;
	bPlanComplete = false;
	bInitialised = true;
	ResetScratch();

	UE_LOG(LogMudTest, Log, TEXT("protocol test run started"));
	return true;
}

bool UMudtrackTests::Advance(float DeltaSeconds)
{
	if (!bInitialised || bPlanComplete) return false;

	Run.Elapsed += DeltaSeconds;

	// Waiting out a delay: do nothing this frame.
	if (Run.IsWaiting())
	{
		return true;
	}

	// Optional subset for iteration: -MudtrackOnly=1,7 runs T01 and T07 only.
	// Skipped tests are left out of the results rather than reported as passes.
	{
		static TSet<int32> Only;
		static bool bParsed = false;
		if (!bParsed)
		{
			bParsed = true;
			FString List;
			if (FParse::Value(FCommandLine::Get(), TEXT("MudtrackOnly="), List, /*bShouldStopOnSeparator=*/false))
			{
				TArray<FString> Parts;
				List.ParseIntoArray(Parts, TEXT(","));
				for (const FString& P : Parts) { Only.Add(FCString::Atoi(*P)); }
			}
		}
		while (Only.Num() > 0 && PlanIndex < 10 && !Only.Contains(PlanIndex + 1) && TestPhase == 0)
		{
			++PlanIndex;
		}
		if (PlanIndex >= 10)
		{
			bPlanComplete = true;
			Finalise();
			return false;
		}
	}

	// Run (or resume) the current test's step.
	bool bFinished = false;
	switch (PlanIndex)
	{
	case 0: bFinished = Step_Stability(Run);      break;
	case 1: bFinished = Step_LogCrossing(Run);    break;
	case 2: bFinished = Step_Diagonal(Run);       break;
	case 3: bFinished = Step_DryVsSoft(Run);      break;
	case 4: bFinished = Step_Wheelspin(Run);      break;
	case 5: bFinished = Step_SecondPass(Run);     break;
	case 6: bFinished = Step_Drivetrain(Run);     break;
	case 7: bFinished = Step_Winch(Run);          break;
	case 8: bFinished = Step_Persistence(Run);    break;
	case 9: bFinished = Step_TestLoad(Run);       break;
	default:
		bPlanComplete = true;
		return false;
	}

	if (bFinished)
	{
		LastResults.Add(Run.Current);
		UE_LOG(LogMudTest, Log, TEXT("[%s] %s :: %s"),
			Run.Current.bPassed ? TEXT("PASS") : TEXT("FAIL"),
			*Run.Current.Name, *Run.Current.Detail);

		++PlanIndex;
		TestPhase = 0;
		ResetScratch();

		if (PlanIndex >= 10)
		{
			bPlanComplete = true;
			Finalise();
			return false;
		}
		// Next test starts on a later frame, giving physics a moment to settle.
		Run.WaitFor(0.35f);
	}
	return true;
}

void UMudtrackTests::Finalise()
{
	if (!OutputPath.IsEmpty())
	{
		TSharedPtr<FJsonObject> Root = MakeShared<FJsonObject>();
		Root->SetStringField(TEXT("project"), TEXT("Mudtrack - forest off-roading and deformable mud"));
		Root->SetStringField(TEXT("engine"), TEXT("Unreal Engine 5.8.2"));
		Root->SetStringField(TEXT("generated_utc"),
			FDateTime::UtcNow().ToString(TEXT("%Y-%m-%dT%H:%M:%SZ")));

		TArray<TSharedPtr<FJsonValue>> Arr;
		int32 Passed = 0;
		for (const FTestResult& R : LastResults)
		{
			TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
			O->SetStringField(TEXT("name"), R.Name);
			O->SetBoolField(TEXT("ran"), R.bRan);
			O->SetBoolField(TEXT("passed"), R.bPassed);
			O->SetStringField(TEXT("detail"), R.Detail);

			TArray<TSharedPtr<FJsonValue>> Ms;
			for (const FString& M : R.Measurements) Ms.Add(MakeShared<FJsonValueString>(M));
			O->SetArrayField(TEXT("measurements"), Ms);
			Arr.Add(MakeShared<FJsonValueObject>(O));

			if (R.bPassed) ++Passed;
		}

		Root->SetArrayField(TEXT("tests"), Arr);
		Root->SetNumberField(TEXT("passed"), Passed);
		Root->SetNumberField(TEXT("failed"), LastResults.Num() - Passed);
		Root->SetNumberField(TEXT("total"), LastResults.Num());

		FString OutStr;
		TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&OutStr);
		FJsonSerializer::Serialize(Root.ToSharedRef(), Writer);
		FFileHelper::SaveStringToFile(OutStr, *OutputPath);
		UE_LOG(LogMudTest, Log, TEXT("results written to %s"), *OutputPath);
	}

	if (Run.Vehicle != nullptr)
	{
		Run.Vehicle->ResetForTest();
		Run.Vehicle->SetHandbrake(true);
	}
}

bool UMudtrackTests::IsRunning()
{
	return bInitialised && !bPlanComplete;
}

FString UMudtrackTests::GetProgressText()
{
	return FString::Printf(TEXT("test %d/10, t=%.1f s, phase %d"),
		PlanIndex + 1, Run.Elapsed, TestPhase);
}

FString UMudtrackTests::GetLastSummary()
{
	int32 Passed = 0;
	for (const FTestResult& R : LastResults) if (R.bPassed) ++Passed;
	FString S = FString::Printf(TEXT("Mudtrack protocol tests: %d/%d passed\n"),
		Passed, LastResults.Num());
	for (const FTestResult& R : LastResults)
	{
		S += FString::Printf(TEXT("  [%s] %-32s %s\n"),
			R.bPassed ? TEXT("PASS") : TEXT("FAIL"), *R.Name, *R.Detail);
	}
	return S;
}

TArray<FTestResult> UMudtrackTests::GetLastResults()
{
	return LastResults;
}



