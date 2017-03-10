using U8 = System.Byte;
using U16 = System.UInt16;
using U32 = System.UInt32;
using U64 = System.UInt64;

namespace ZPAQSharp
{
	// Virtual base classes for input and output
	// get() and put() must be overridden to read or write 1 byte.
	// read() and write() may be overridden to read or write n bytes more
	// efficiently than calling get() or put() n times.
	class Writer
	{
		public virtual void put(int c) // should output low 8 bits of c
		{
		}

		public virtual void write(string buf, int n) // write buf[n]
		{
			for (int i = 0; i < n; ++i)
			{
				put((U8)buf[i]);
			}
		}

		~Writer()
		{
		}
	}
}
