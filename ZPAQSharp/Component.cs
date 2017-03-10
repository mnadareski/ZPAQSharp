using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using U8 = System.Byte;
using U16 = System.UInt16;
using U32 = System.UInt32;
using U64 = System.UInt64;

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
		public Array<U32> cm;  // cm[cxt] -> p in bits 31..10, n in 9..0; MATCH index
		public Array<U8> ht;   // ICM/ISSE hash table[0..size1][0..15] and MATCH buf
		public Array<U16> a16; // MIX weights

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
			cm.resize(0);
			ht.resize(0);
			a16.resize(0);
		}

		public Component()
		{
			init();
		}
	}
}
