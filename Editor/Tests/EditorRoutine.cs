using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>
    /// Steps a coroutine one yield per editor update (nested enumerators run to their end first), so long work such as a
    /// case replay leaves the editor responsive between its steps: the refit computes on ReFit's worker thread meanwhile
    /// (<see cref="ReFitEngine.RunCoroutine(ReFitRequest, ReFitProgress, Action{ReFitComputation})"/>), and other tools'
    /// calls into the editor (MCP agents) get through. <see cref="Drain"/> runs one to its end at once for synchronous callers.
    /// </summary>
    internal static class EditorRoutine
    {
        // Quick steps run together within an update, up to this long.
        private const long SliceMs = 10;

        public static void Start(IEnumerator routine, Action<Exception> failed)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(routine);
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                try
                {
                    var watch = Stopwatch.StartNew();
                    while (stack.Count > 0)
                    {
                        var top = stack.Peek();
                        if (!top.MoveNext()) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
                        if (top.Current is IEnumerator nested) { stack.Push(nested); continue; }
                        if (watch.ElapsedMilliseconds >= SliceMs) break;
                    }
                    if (stack.Count == 0) EditorApplication.update -= tick;
                    else EditorApplication.QueuePlayerLoopUpdate();
                }
                catch (Exception e)
                {
                    EditorApplication.update -= tick;
                    while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
                    failed?.Invoke(e);
                }
            };
            EditorApplication.update += tick;
            EditorApplication.QueuePlayerLoopUpdate();
        }

        public static void Drain(IEnumerator routine)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(routine);
            try
            {
                while (stack.Count > 0)
                {
                    var top = stack.Peek();
                    if (!top.MoveNext()) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
                    if (top.Current is IEnumerator nested) stack.Push(nested);
                }
            }
            finally { while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose(); }
        }
    }
}
