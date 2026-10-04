// Drivetrain.cpp

#include "Drivetrain.h"
#include "Math/UnrealMathUtility.h"

float SampleEngineTorque(const FEngineSpec& Spec, float RPM)
{
	const float R = FMath::Clamp(RPM, Spec.StallRPM, Spec.MaxRPM);

	// Piecewise torque curve: rises to peak, then falls off toward the limiter.
	// Idle region is deliberately weak so stalling in mud is a real risk.
	const float IdleFactor = FMath::Clamp((R - Spec.StallRPM) / FMath::Max(Spec.IdleRPM - Spec.StallRPM, 1.f), 0.f, 1.f);

	if (R <= Spec.PeakTorqueRPM)
	{
		// Ramp up from idle to peak.
		const float T = (R - Spec.StallRPM) / FMath::Max(Spec.PeakTorqueRPM - Spec.StallRPM, 1.f);
		// Slightly concave so low revs do not feel mushy.
		const float Shape = FMath::Pow(FMath::Clamp(T, 0.f, 1.f), 0.75f);
		return Spec.PeakTorqueNcm * FMath::Lerp(0.35f, 1.0f, Shape) * FMath::Lerp(0.25f, 1.0f, IdleFactor);
	}

	// Falling side: torque tapers to a little under half at the limiter.
	const float T = (R - Spec.PeakTorqueRPM) / FMath::Max(Spec.MaxRPM - Spec.PeakTorqueRPM, 1.f);
	const float Fall = FMath::Lerp(1.0f, 0.55f, FMath::Clamp(T, 0.f, 1.f));
	return Spec.PeakTorqueNcm * Fall;
}

void SplitAxleTorque(float InTorque, float OmegaA, float OmegaB, EDiffLock Lock,
                     float LockPreload, float& OutTorqueA, float& OutTorqueB)
{
	OutTorqueA = 0.f;
	OutTorqueB = 0.f;

	const float Half = InTorque * 0.5f;

	switch (Lock)
	{
	case EDiffLock::Open:
	{
		// An open differential is a torque-balancing device: it can only ever
		// deliver equal torque to both outputs. The split is therefore the
		// *minimum* the two sides can take. Here we approximate the available
		// tyre torque with a speed-based proxy: the faster-spinning wheel has
		// broken traction, so the axle saturates at what it can absorb.
		//
		// We do not know the tyre limits here, so the caller passes the axle
		// torque it wants and we simply split evenly — the traction limit is
		// enforced per-wheel in the tyre model. What makes the open diff behave
		// correctly is that the *speed* of both wheels is THEN coupled by the
		// caller: an open diff lets them differ, a locked one forces them equal.
		OutTorqueA = Half;
		OutTorqueB = Half;
		break;
	}
	case EDiffLock::LimitedSlip:
	{
		// Clutch-type LSD: bias torque toward the slower wheel up to a ratio.
		const float Bias = 2.6f;               // typical for a plate LSD
		const float Sum = FMath::Abs(OmegaA) + FMath::Abs(OmegaB) + 1e-3f;
		const float SlowSide = (FMath::Abs(OmegaA) < FMath::Abs(OmegaB)) ? 0.f : 1.f;

		float TA = Half;
		float TB = Half;
		if (SlowSide < 0.5f)
		{
			// A is slower -> give it more.
			TA = InTorque * (Bias / (1.f + Bias));
			TB = InTorque * (1.f / (1.f + Bias));
		}
		else
		{
			TB = InTorque * (Bias / (1.f + Bias));
			TA = InTorque * (1.f / (1.f + Bias));
		}
		// Add the preload, which helps even at zero speed difference.
		TA += LockPreload * 0.5f;
		TB += LockPreload * 0.5f;
		OutTorqueA = TA;
		OutTorqueB = TB;
		break;
	}
	case EDiffLock::Locked:
	default:
	{
		// Locked: both outputs are rigidly coupled. They share speed, so torque
		// may go entirely to one side. The caller forces the speeds equal; here
		// we deliver the full requested axle torque to the pair.
		OutTorqueA = Half;
		OutTorqueB = Half;
		break;
	}
	}
}
