using System;

namespace DotNES.Core
{
    [AttributeUsage(AttributeTargets.Method)]
    class OpCodeAttribute : Attribute
    {
        public byte opcode { get; set; }
        public string name { get; set; }
        public int bytes { get; set; }
    }
}
