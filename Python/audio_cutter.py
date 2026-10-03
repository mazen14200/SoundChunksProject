#!/usr/bin/env python3
"""
Audio Cutter CLI
Cuts audio segments from input file and outputs as MP3 using FFmpeg.
"""

import argparse
import sys
import subprocess
import os
from pathlib import Path


def parse_arguments():
    """Parse command line arguments."""
    parser = argparse.ArgumentParser(
        description="Cut audio segment from input file and output as MP3",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter
    )
    
    parser.add_argument(
        "--input",
        required=True,
        help="Path to input audio file"
    )
    
    parser.add_argument(
        "--output",
        required=True,
        help="Path to output MP3 file"
    )
    
    parser.add_argument(
        "--start",
        type=float,
        required=True,
        help="Start time in seconds"
    )
    
    parser.add_argument(
        "--end",
        type=float,
        required=True,
        help="End time in seconds"
    )
    
    parser.add_argument(
        "--ffmpeg",
        default="ffmpeg",
        help="Path to FFmpeg executable"
    )
    
    parser.add_argument(
        "--bitrate",
        default="192k",
        help="MP3 bitrate"
    )
    
    return parser.parse_args()


def validate_arguments(args):
    """Validate command line arguments."""
    # Check input file exists
    if not os.path.exists(args.input):
        print(f"Error: Input file does not exist: {args.input}", file=sys.stderr)
        return False
    
    # Validate start/end times
    if args.start < 0:
        print(f"Error: Start time must be non-negative, got {args.start}", file=sys.stderr)
        return False
    
    if args.end <= args.start:
        print(f"Error: End time ({args.end}) must be greater than start time ({args.start})", file=sys.stderr)
        return False
    
    # Ensure output directory exists
    output_dir = os.path.dirname(args.output)
    if output_dir and not os.path.exists(output_dir):
        try:
            os.makedirs(output_dir, exist_ok=True)
        except Exception as e:
            print(f"Error: Cannot create output directory: {e}", file=sys.stderr)
            return False
    
    return True


def cut_audio(args):
    """Cut audio segment using FFmpeg."""
    try:
        # Build FFmpeg command
        # -ss: start time
        # -to: end time (duration from start)
        # -i: input file
        # -c:a libmp3lame: encode as MP3
        # -b:a: bitrate
        # -y: overwrite output file if exists
        duration = args.end - args.start
        
        cmd = [
            args.ffmpeg,
            "-ss", str(args.start),
            "-i", args.input,
            "-t", str(duration),
            "-c:a", "libmp3lame",
            "-b:a", args.bitrate,
            "-y",
            args.output
        ]
        
        print(f"Executing: {' '.join(cmd)}", file=sys.stderr)
        
        # Run FFmpeg
        result = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            check=True
        )
        
        # Check if output file was created
        if not os.path.exists(args.output):
            print("Error: FFmpeg completed but output file was not created", file=sys.stderr)
            return False
        
        # Check if output file has content
        if os.path.getsize(args.output) == 0:
            print("Error: Output file is empty", file=sys.stderr)
            os.remove(args.output)
            return False
        
        print(f"Successfully created: {args.output}", file=sys.stderr)
        return True
        
    except subprocess.CalledProcessError as e:
        print(f"FFmpeg error: {e}", file=sys.stderr)
        if e.stdout:
            print(f"FFmpeg stdout: {e.stdout}", file=sys.stderr)
        if e.stderr:
            print(f"FFmpeg stderr: {e.stderr}", file=sys.stderr)
        return False
    except Exception as e:
        print(f"Error cutting audio: {e}", file=sys.stderr)
        return False


def main():
    """Main entry point."""
    args = parse_arguments()
    
    if not validate_arguments(args):
        sys.exit(1)
    
    if cut_audio(args):
        sys.exit(0)
    else:
        sys.exit(1)


if __name__ == "__main__":
    main()
