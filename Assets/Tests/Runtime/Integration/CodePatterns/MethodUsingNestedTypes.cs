using UnityEngine;

namespace FastScriptReload.Tests.Runtime.Integration.CodePatterns
{
    public class MethodUsingNestedTypes : MonoBehaviour
    {
        private class Leg
        {
            public readonly int length;

            public Leg(int length)
            {
                this.length = length;
            }

            public int Twice()
            {
                //<mock-runtime-code-change>// return length * 3;
                return length * 2;
            }

            public Leg Me()
            {
                //<mock-runtime-code-change>// return Identity(this);
                return this;
            }

            private static Leg Identity(Leg leg) => leg;
        }

        private readonly struct Point
        {
            public readonly int x;

            public Point(int x)
            {
                this.x = x;
            }
        }

        private readonly Leg kept = new Leg(1);
        private readonly object boxed = new Point(1);

        /// <summary>Patched, creates nested instances that must be the original types, as the unpatched fields hold.</summary>
        public void UseNestedTypes()
        {
            //<mock-runtime-code-change>// var made = new Leg(3);
            //<mock-runtime-code-change>// TestDetourConfirmation.Confirm(GetType(), nameof(UseNestedTypes), made.GetType() == kept.GetType() && boxed is Point && made.Me() == made);
        }

        /// <summary>The nested class's own method, called on an instance the original code created.</summary>
        public int KeptTwice() => kept.Twice();
    }
}
