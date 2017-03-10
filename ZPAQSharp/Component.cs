using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using byte = System.Byte;
using ushort = System.UInt16;
using uint = System.UInt32;
using ulong = System.UInt64;

namespace ZPAQSharp
{
	// A Component is a context model, indirect context model, match model,
	// fixed weight mixer, adaptive 2 input mixer without or with current
	// partial byte as context, adaptive m input mixer (without or with),
	// or SSE (without or with).
	class Component
	{
		public ulong limit;   // max count for cm
		public ulong cxt;     // saved context
		public ulong a, b, c; // multi-purpose variables
		public uint[] cm;  // cm[cxt] . p in bits 31..10, n in 9..0; MATCH index
		public byte[] ht;   // ICM/ISSE hash table[0..size1][0..15] and MATCH buf
		public ushort[] a16; // MIX weights

		public static int[] compsize = new int[256] {
			0, 2, 3, 2, 3, 4, 6, 6, 3, 5, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

		public void init()    // initialize to all 0
		{
			limit = cxt = a = b = c = 0;
			Array.Resize(ref cm, 0);
			Array.Resize(ref ht, 0);
			Array.Resize(ref a16, 0);
		}

		public Component()
		{
			init();
		}
	}
}
