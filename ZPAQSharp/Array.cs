using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZPAQSharp
{
	// An Array of T is cleared and aligned on a 64 byte address
	//   with no constructors called. No copy or assignment.
	// Array<T> a(n, ex=0);  - creates n<<ex elements of type T
	// a[i] - index
	// a(i) - index mod n, n must be a power of 2
	// a.size() - gets n
	class Array<T>
	{
		T[] data;	// user location of [0] on a 64 byte boundary
		ulong n;     // user size
		int offset; // distance back in bytes to start of actual allocation

		void operator=(Array other); // no assignment

		Array(Array other) // no copy
		{
		}

		public Array(ulong sz = 0, int ex = 0) // [0..sz-1] = 
		{
			data = null;
			n = 0;
			offset = 0;

			resize(sz, ex);
		}

		public unsafe void resize(ulong sz, int ex = 0) // change size, erase content to zeros
		{
			unchecked
			{
				Debug.Assert((ulong)-1 > 0);
			}
			
			while (ex > 0)
			{
				if (sz > sz * 2)
				{
					LibZPAQ.error("Array too big");
				}

				sz *= 2;
				--ex;
			}

			if (n > 0)
			{
				Debug.Assert(offset > 0 && offset <= 64);
				// Debug.Assert(sizeof(T) - offset != 0);
				// ::free((char*)data - offset);
			}

			n = 0;
			offset = 0;
			if (sz == 0)
			{
				return;
			}

			n = sz;
			ulong nb = 128 + n * sizeof(T); // test for overflow
			if (nb <= 128 || (nb - 128) / sizeof(T) != n)
			{
				n = 0;
				LibZPAQ.error("Array too big");
			}

			data = (T[])calloc(nb, 1);
			if (data == null)
			{
				n = 0;
				LibZPAQ.error("Out of memory");
			}

			offset = 64 - (((char*)data - (char*)0) & 63);
			Debug.Assert(offset > 0 && offset <= 64);
			data = (T[])((char*)data + offset);
		}

		~Array() // free memory
		{
			resize(0);
		}

		public ulong size() // get size
		{
			return n;
		}

		public int isize() // get size as an int
		{
			return (int)n;
		}

		public T this[ulong i]
		{
			get
			{
				Debug.Assert(n > 0 && i < n);
				return data[i];
			}
			set
			{
				Debug.Assert(n > 0 && i < n);
				data[i] = value;
			}
		}

		public T get(ulong i) // Replacing operator()
		{
			Debug.Assert(n > 0 && (n & (n - 1)) == 0);
			return data[i & (n - 1)];
		}
	}
}
