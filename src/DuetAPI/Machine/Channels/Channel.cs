﻿using System;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about a G/M/T-code channel
    /// </summary>
    public class Channel : ICloneable
    {
        /// <summary>
        /// Current feedrate in mm/s
        /// </summary>
        public float Feedrate { get; set; } = 50;

        /// <summary>
        /// Whether relative extrusion is being used
        /// </summary>
        public bool RelativeExtrusion { get; set; } = true;

        /// <summary>
        /// Whether volumetric extrusion is being used
        /// </summary>
        public bool VolumetricExtrusion { get; set; }

        /// <summary>
        /// Whether relative positioning is being used
        /// </summary>
        public bool RelativePositioning { get; set; }

        /// <summary>
        /// Whether inches are being used instead of mm
        /// </summary>
        public bool UsingInches { get; set; }

        /// <summary>
        /// Depth of the stack
        /// </summary>
        public byte StackDepth;

        /// <summary>
        /// Number of the current line
        /// </summary>
        public long LineNumber;

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Channel
            {
                Feedrate = Feedrate,
                RelativeExtrusion = RelativeExtrusion,
                RelativePositioning = RelativePositioning,
                UsingInches = UsingInches,
                StackDepth = StackDepth,
                LineNumber = LineNumber
            };
        }
    }
}
