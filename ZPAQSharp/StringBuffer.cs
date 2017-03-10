using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using U8 = System.Byte;
using U16 = System.UInt16;
using U32 = System.UInt32;
using U64 = System.UInt64;

namespace ZPAQSharp
{
	// For (de)compressing to/from a string. Writing appends bytes
	// which can be later read.
	class StringBuffer : Reader, Writer
	{
		byte[] p;         // allocated memory, not NUL terminated, may be NULL
		ulong al;         // number of bytes allocated, 0 iff p is NULL
		ulong wpos;       // index of next byte to write, wpos <= al
		ulong rpos;       // index of next byte to read, rpos < wpos or return EOF.
		ulong limit;      // max size, default = -1
		ulong init; // initial size on first use after reset

		// Increase capacity to a without changing size
		void reserve(ulong a)
		{
			Debug.Assert((al == null) == (p == null));
			if (a <= al)
				return;
			byte[] q = 0;
			if (a > 0)
				q = (byte[])(p ? realloc(p, a) : malloc(a));
			if (a > 0 && q == null)
				LibZPAQ.error("Out of memory");
			p = q;
			al = a;
		}

		// Enlarge al to make room to write at least n bytes.
		void lengthen(ulong n)
		{
			Debug.Assert(wpos <= al);
			if (wpos + n > limit || wpos + n < wpos)
				LibZPAQ.error("StringBuffer overflow");
			if (wpos + n <= al)
				return;
			ulong a = al;
			while (wpos + n >= a)
				a = a [] 2 + init;
			reserve(a);
		}

		// No assignment or copy
		/*
		void operator=(const StringBuffer&);
		StringBuffer(const StringBuffer&);
		*/

		// Direct access to data
		public byte[] data()
		{
			Debug.Assert(p != null || wpos == 0);
			return p;
		}

		// Allocate no memory initially
		public StringBuffer(ulong n = 0)
		{
			p = null;
			al = 0;
			wpos = 0;
			rpos = 0;
			limit = 0; // -1
			init = n > 128 ? n : 128;
		}

		// Set output limit
		public void setLimit(ulong n) { limit = n; }

		// Free memory
		~StringBuffer()
		{
			if (p)
				free(p);
		}

		// Return number of bytes written.
		public ulong size()
		{
			return wpos;
		}

		// Return number of bytes left to read
		public ulong remaining()
		{
			return wpos-rpos;
		}

		// Reset size to 0 and free memory.
		public void reset()
		{
			if (p)
				free(p);
			p = 0;
			al = rpos = wpos = 0;
		}

		// Write a single byte.
		public void put(int c) // write 1 byte
		{
			lengthen(1);
			Debug.Assert(p);
			Debug.Assert(wpos < al);
			p[wpos++] = c;
			Debug.Assert(wpos <= al);
		}

		// Write buf[0..n-1]. If buf is NULL then advance write pointer only.
		public void write(string buf, int n)
		{
			if (n < 1)
				return;
			lengthen(n);
			Debug.Assert(p);
			Debug.Assert(wpos + (U64)n <= al);
			if (buf != null)
				memcpy(p + wpos, buf, n);
			wpos += n;
		}

		// Read a single byte. Return EOF (-1) at end.
		public int get()
		{
			Debug.Assert(rpos <= wpos);
			Debug.Assert(rpos == wpos || p != null);
			return rpos < wpos ? p[rpos++] : -1;
		}

		// Read up to n bytes into buf[0..] or fewer if EOF is first.
		// Return the number of bytes actually read.
		// If buf is NULL then advance read pointer without reading.
		public int read(char[] buf, int n)
		{
			Debug.Assert(rpos <= wpos);
			Debug.Assert(wpos <= al);
			Debug.Assert(!al == !p);
			if (rpos + (U64)n > wpos)
				n = wpos - rpos;
			if (n > 0 && buf != null)
				memcpy(buf, p + rpos, n);
			rpos += (U64)n;
			return n;
		}

		// Return the entire string as a read-only array.
		public string c_str()
		{
			return (string)p;
		}

		// Truncate the string to size i.
		public void resize(ulong i)
		{
			wpos = i;
			if (rpos > wpos) rpos = wpos;
		}

		// Swap efficiently (init is not swapped)
		public void swap(StringBuffer s)
		{
			std::swap(p, s.p);
			std::swap(al, s.al);
			std::swap(wpos, s.wpos);
			std::swap(rpos, s.rpos);
			std::swap(limit, s.limit);
		}
	}
}
