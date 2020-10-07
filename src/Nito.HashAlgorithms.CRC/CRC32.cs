﻿using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;

namespace Nito.HashAlgorithms
{
    /// <summary>
    /// A generalized CRC-32 algorithm.
    /// </summary>
    public sealed class CRC32 : HashAlgorithmBase
    {
        /// <summary>
        /// The lookup tables for non-reversed polynomials.
        /// </summary>
        private static readonly ConcurrentDictionary<uint, uint[]> NormalLookupTables = new ConcurrentDictionary<uint, uint[]>();
        
        /// <summary>
        /// The lookup tables for reversed polynomials.
        /// </summary>
        private static readonly ConcurrentDictionary<uint, uint[]> ReversedLookupTables = new ConcurrentDictionary<uint, uint[]>();

        /// <summary>
        /// A reference to the lookup table.
        /// </summary>
        private readonly uint[] _lookupTable;

        /// <summary>
        /// The CRC-32 algorithm definition.
        /// </summary>
        private readonly Definition _definition;

        /// <summary>
        /// The current value of the remainder.
        /// </summary>
        private uint _remainder;

        /// <summary>
        /// Initializes a new instance of the <see cref="CRC32"/> class with the specified definition and lookup table.
        /// </summary>
        /// <param name="definition">The CRC-32 algorithm definition. May not be <c>null</c>.</param>
        /// <param name="lookupTable">The lookup table. May not be <c>null</c>.</param>
        public CRC32(Definition definition, uint[] lookupTable)
            :base(32)
        {
            _ = definition ?? throw new ArgumentNullException(nameof(definition));
            _ = lookupTable ?? throw new ArgumentNullException(nameof(lookupTable));
            if (lookupTable.Length != 256)
                throw new ArgumentException($"{nameof(lookupTable)} must have 256 entries, but it has {lookupTable.Length} entries.", nameof(lookupTable));

            _definition = definition;
            _lookupTable = lookupTable;
            Initialize();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CRC32"/> class with the specified definition.
        /// </summary>
        /// <param name="definition">The CRC-32 algorithm definition. May not be <c>null</c>.</param>
        public CRC32(Definition definition)
            : this(definition, FindOrGenerateLookupTable(definition))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CRC32"/> class with the default definition. Note that the "default" CRC-32 definition is an older IEEE recommendation and there are better polynomials for new protocols.
        /// </summary>
        public CRC32()
            : this(Definition.Default)
        {
        }

        /// <summary>
        /// Gets the result of the CRC-32 algorithm.
        /// </summary>
        public uint Result
        {
            get
            {
                if (_definition.ReverseResultBeforeFinalXor != _definition.ReverseDataBytes)
                {
                    return HackersDelight.Reverse(_remainder) ^ _definition.FinalXorValue;
                }
                else
                {
                    return _remainder ^ _definition.FinalXorValue;
                }
            }
        }

        /// <summary>
        /// Searches the known lookup tables for one matching the given CRC-32 definition; if none is found, a new lookup table is generated and added to the known lookup tables.
        /// </summary>
        /// <param name="definition">The CRC-32 definition.</param>
        /// <returns>The lookup table for the given CRC-32 definition.</returns>
        public static uint[] FindOrGenerateLookupTable(Definition definition)
        {
            _ = definition ?? throw new ArgumentNullException(nameof(definition));

            ConcurrentDictionary<uint, uint[]> tables;
            if (definition.ReverseDataBytes)
            {
                tables = ReversedLookupTables;
            }
            else
            {
                tables = NormalLookupTables;
            }

            var ret = tables.GetOrAdd(definition.TruncatedPolynomial, _ => GenerateLookupTable(definition));
            return ret;
        }

        /// <summary>
        /// Generates a lookup table for a CRC-32 algorithm definition. Both <see cref="Definition.TruncatedPolynomial"/> and <see cref="Definition.ReverseDataBytes"/> are used in the calculations.
        /// </summary>
        /// <param name="definition">The CRC-32 algorithm definition.</param>
        /// <returns>The lookup table.</returns>
        public static uint[] GenerateLookupTable(Definition definition)
        {
            _ = definition ?? throw new ArgumentNullException(nameof(definition));

            unchecked
            {
                uint[] ret = new uint[256];

                byte dividend = 0;
                do
                {
                    uint remainder = 0;

                    for (byte mask = 0x80; mask != 0; mask >>= 1)
                    {
                        if ((dividend & mask) != 0)
                        {
                            remainder ^= 0x80000000;
                        }

                        if ((remainder & 0x80000000) != 0)
                        {
                            remainder <<= 1;
                            remainder ^= definition.TruncatedPolynomial;
                        }
                        else
                        {
                            remainder <<= 1;
                        }
                    }

                    if (definition.ReverseDataBytes)
                    {
                        var index = HackersDelight.Reverse(dividend);
                        ret[index] = HackersDelight.Reverse(remainder);
                    }
                    else
                    {
                        ret[dividend] = remainder;
                    }

                    ++dividend;
                }
                while (dividend != 0);

                return ret;
            }
        }

        /// <summary>
        /// Initializes the CRC-32 calculations.
        /// </summary>
        public override void Initialize()
        {
            if (_definition.ReverseDataBytes)
            {
                _remainder = HackersDelight.Reverse(_definition.Initializer);
            }
            else
            {
                _remainder = _definition.Initializer;
            }
        }

        /// <summary>
        /// Returns a <see cref="System.String"/> that represents this instance.
        /// </summary>
        /// <returns>A <see cref="System.String"/> that represents this instance.</returns>
        public override string ToString()
        {
            return "0x" + Result.ToString("X8", CultureInfo.InvariantCulture);
        }

        /// <inheritdoc />
        protected override void DoHashCore(ReadOnlySpan<byte> source)
        {
            unchecked
            {
                uint remainder = _remainder;
                for (int i = 0; i != source.Length; ++i)
                {
                    byte index = ReflectedIndex(remainder, source[i]);
                    remainder = ReflectedShift(remainder);
                    remainder ^= _lookupTable[index];
                }

                _remainder = remainder;
            }
        }

        /// <inheritdoc />
        protected override void DoHashFinal(Span<byte> destination) => BinaryPrimitives.WriteUInt32LittleEndian(destination, Result);

        /// <summary>
        /// Gets the index into the lookup array for a given remainder and data byte. Data byte reversal is taken into account.
        /// </summary>
        /// <param name="remainder">The current remainder.</param>
        /// <param name="data">The data byte.</param>
        /// <returns>The index into the lookup array.</returns>
        private byte ReflectedIndex(uint remainder, byte data)
        {
            unchecked
            {
                if (_definition.ReverseDataBytes)
                {
                    return (byte)(remainder ^ data);
                }
                else
                {
                    return (byte)((remainder >> 24) ^ data);
                }
            }
        }

        /// <summary>
        /// Shifts a byte out of the remainder. This is the high byte or low byte, depending on whether the data bytes are reversed.
        /// </summary>
        /// <param name="remainder">The remainder value.</param>
        /// <returns>The shifted remainder value.</returns>
        private uint ReflectedShift(uint remainder)
        {
            unchecked
            {
                if (_definition.ReverseDataBytes)
                {
                    return remainder >> 8;
                }
                else
                {
                    return remainder << 8;
                }
            }
        }

        /// <summary>
        /// Holds parameters for a CRC-32 algorithm.
        /// </summary>
        public sealed class Definition
        {
            /// <summary>
            /// Gets a CRC-32 defined by the old IEEE standard; used by Ethernet, zip, PNG, etc. Note that this "default" CRC-32 definition is an older IEEE recommendation and there are better polynomials for new protocols. Known as "CRC-32", "CRC-32/ADCCP", and "PKZIP".
            /// </summary>
            public static Definition Default
            {
                get
                {
                    return new Definition
                    {
                        TruncatedPolynomial = 0x04C11DB7,
                        Initializer = 0xFFFFFFFF,
                        FinalXorValue = 0xFFFFFFFF,
                        ReverseDataBytes = true,
                        ReverseResultBeforeFinalXor = true,
                    };
                }
            }

            /// <summary>
            /// Gets a CRC-32 used by BZIP2. Known as "CRC-32/BZIP2" and "B-CRC-32".
            /// </summary>
            public static Definition BZip2
            {
                get
                {
                    return new Definition
                    {
                        TruncatedPolynomial = 0x04C11DB7,
                        Initializer = 0xFFFFFFFF,
                        FinalXorValue = 0xFFFFFFFF,
                    };
                }
            }

            /// <summary>
            /// Gets a modern CRC-32 defined in RFC 3720. Known as "CRC-32C", "CRC-32/ISCSI", and "CRC-32/CASTAGNOLI".
            /// </summary>
            public static Definition Castagnoli
            {
                get
                {
                    return new Definition
                    {
                        TruncatedPolynomial = 0x1EDC6F41,
                        Initializer = 0xFFFFFFFF,
                        FinalXorValue = 0xFFFFFFFF,
                        ReverseDataBytes = true,
                        ReverseResultBeforeFinalXor = true,
                    };
                }
            }

            /// <summary>
            /// Gets a CRC-32 used by the MPEG-2 standard. Known as "CRC-32/MPEG-2".
            /// </summary>
            public static Definition Mpeg2
            {
                get
                {
                    return new Definition
                    {
                        TruncatedPolynomial = 0x04C11DB7,
                        Initializer = 0xFFFFFFFF,
                    };
                }
            }

            /// <summary>
            /// Gets a CRC-32 used by the POSIX "chksum" command; note that the chksum command-line program appends the file length to the contents unless it is empty. Known as "CRC-32/POSIX" and "CKSUM".
            /// </summary>
            public static Definition Posix
            {
                get
                {
                    return new Definition
                    {
                        TruncatedPolynomial = 0x04C11DB7,
                        FinalXorValue = 0xFFFFFFFF,
                    };
                }
            }

            /// <summary>
            /// Gets a CRC-32 used in the Aeronautical Information eXchange Model. Known as "CRC-32Q".
            /// </summary>
            public static Definition Aixm
            {
                get
                {
                    return new Definition
                    {
                        TruncatedPolynomial = 0x814141AB,
                    };
                }
            }

            /// <summary>
            /// Gets a very old CRC-32, appearing in "Numerical Recipes in C". Known as "XFER".
            /// </summary>
            public static Definition Xfer
            {
                get
                {
                    return new Definition
                    {
                        TruncatedPolynomial = 0x000000AF,
                    };
                }
            }

            /// <summary>
            /// Gets or sets the normal (non-reversed, non-reciprocal) polynomial to use for the CRC calculations.
            /// </summary>
            public uint TruncatedPolynomial { get; set; }

            /// <summary>
            /// Gets or sets the value to which the remainder is initialized at the beginning of the CRC calculation.
            /// </summary>
            public uint Initializer { get; set; }

            /// <summary>
            /// Gets or sets the value by which the remainder is XOR'ed at the end of the CRC calculation.
            /// </summary>
            public uint FinalXorValue { get; set; }

            /// <summary>
            /// Gets or sets a value indicating whether incoming data bytes are reversed/reflected.
            /// </summary>
            public bool ReverseDataBytes { get; set; }

            /// <summary>
            /// Gets or sets a value indicating whether the final remainder is reversed/reflected at the end of the CRC calculation before it is XOR'ed with <see cref="FinalXorValue"/>.
            /// </summary>
            public bool ReverseResultBeforeFinalXor { get; set; }

            /// <summary>
            /// Creates a new <see cref="CRC32"/> instance using this definition.
            /// </summary>
            /// <returns>A new <see cref="CRC32"/> instance using this definition.</returns>
            public CRC32 Create()
            {
                return new CRC32(this);
            }
        }
    }
}
