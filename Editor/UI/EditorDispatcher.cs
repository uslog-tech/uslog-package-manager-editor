using System;
using System.Collections.Concurrent;
using UnityEditor;
using UnityEngine;

namespace Uslog.PackageManager.Editor
{
    /// <summary>
    /// メインスレッドへ戻す口。
    ///
    /// 通信は HttpClient で行うので、await の続きがどのスレッドで
    /// 動くかは保証されない。UI の更新も AssetDatabase も、
    /// メインスレッド以外から触ると落ちるか黙って壊れる。
    ///
    /// 「await の後は必ずここを通してから UI を触る」ことにして、
    /// SynchronizationContext の挙動に頼らない。
    /// </summary>
    [InitializeOnLoad]
    internal static class EditorDispatcher
    {
        private static readonly ConcurrentQueue<Action> Pending = new ConcurrentQueue<Action>();

        static EditorDispatcher()
        {
            EditorApplication.update += Pump;
        }

        public static void Run(Action action)
        {
            if (action == null) return;
            Pending.Enqueue(action);
        }

        private static void Pump()
        {
            while (Pending.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    // 1 つの失敗で後続を止めない。ここで握り潰すと
                    // 「押しても何も起きない」になるので、必ずログに出す。
                    Debug.LogException(exception);
                }
            }
        }
    }
}
