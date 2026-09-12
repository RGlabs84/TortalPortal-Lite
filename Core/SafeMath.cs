using System;
using UnityEngine;

namespace TortalPortalLite.Core
{
    public static class SafeMath
    {
        public static float Safe(float value, float fallback = 0f)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return fallback;
            return value;
        }

        public static float ClampSafe(float value, float min, float max, float fallback = 0f)
        {
            float safeVal = Safe(value, fallback);
            return Mathf.Clamp(safeVal, min, max);
        }

        public static Vector3 SafeVector(Vector3 vec)
        {
            return new Vector3(Safe(vec.x), Safe(vec.y), Safe(vec.z));
        }
    }
}
