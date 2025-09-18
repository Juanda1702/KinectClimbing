using System;
using System.Reflection;
using UnityEngine;

public static class KinectHelpers
{
    /// Intenta obtener el userId del “jugador 1” con distintas firmas del KinectManager.
    public static bool TryGetFirstUserId(object kinectManagerObj, out uint userId)
    {
        userId = 0u;
        if (kinectManagerObj == null) return false;
        var t = kinectManagerObj.GetType();

        // bool IsUserDetected()
        var isDetected = t.GetMethod("IsUserDetected", Type.EmptyTypes);
        if (isDetected != null && !(bool)isDetected.Invoke(kinectManagerObj, null)) return false;

        // uint GetUserIdByIndex(int)
        var getByIndex = t.GetMethod("GetUserIdByIndex", new[] { typeof(int) });
        if (getByIndex != null)
        {
            object idObj = getByIndex.Invoke(kinectManagerObj, new object[] { 0 });
            uint u = idObj is uint uu ? uu : (idObj is ulong gu ? (uint)gu : 0u);
            if (u != 0) { userId = u; return true; }
        }

        // uint GetPlayer1ID()
        var getP1 = t.GetMethod("GetPlayer1ID", Type.EmptyTypes);
        if (getP1 != null)
        {
            object idObj = getP1.Invoke(kinectManagerObj, null);
            uint u = idObj is uint uu ? uu : (idObj is ulong gu ? (uint)gu : 0u);
            if (u != 0) { userId = u; return true; }
        }

        // Property Player1ID / player1ID
        var p1Prop = t.GetProperty("Player1ID") ?? t.GetProperty("player1ID");
        if (p1Prop != null)
        {
            object idObj = p1Prop.GetValue(kinectManagerObj, null);
            uint u = idObj is uint uu ? uu : (idObj is ulong gu ? (uint)gu : 0u);
            if (u != 0) { userId = u; return true; }
        }

        // Fallback: GetUsersCount + GetUserIdByIndex(i)
        var usersCount = t.GetMethod("GetUsersCount", Type.EmptyTypes);
        if (usersCount != null && getByIndex != null)
        {
            int n = (int)usersCount.Invoke(kinectManagerObj, null);
            for (int i = 0; i < n; i++)
            {
                object idObj = getByIndex.Invoke(kinectManagerObj, new object[] { i });
                uint u = idObj is uint uu ? uu : (idObj is ulong gu ? (uint)gu : 0u);
                if (u != 0) { userId = u; return true; }
            }
        }

        return false;
    }
}