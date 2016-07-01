﻿using OpenTK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.NTROSerialization;

namespace GUI.Types.Renderer.Animation
{
    internal class Animation
    {
        public string Name { get; private set; }
        public float Fps { get; private set; }

        private int FrameCount;

        private Frame[] Frames;

        private Skeleton Skeleton;

        // Build animation from resource
        public Animation(Resource resource, NTROStruct decodeKey)
        {
            Name = string.Empty;
            Fps = 0;
            Frames = new Frame[0];

            //Skeleton = skeleton;

            var animationData = (NTRO)resource.Blocks[BlockType.DATA];
            var animArray = (NTROArray)animationData.Output["m_animArray"];

            if (animArray.Count == 0)
            {
                Console.WriteLine("Empty animation file found.");
                return;
            }

            var decoderArray = MakeDecoderArray((NTROArray)animationData.Output["m_decoderArray"]);
            var segmentArray = (NTROArray)animationData.Output["m_segmentArray"];

            // Get the first animation description
            ConstructFromDesc(animArray.Get<NTROStruct>(0), decodeKey, decoderArray, segmentArray);

            return;
        }

        public float[] GetAnimationMatricesAsArray(float time, Skeleton skeleton)
        {
            var matrices = GetAnimationMatrices(time, skeleton);
            return Flatten(matrices);
        }

        // Get the animation matrix for each bone
        public Matrix4[] GetAnimationMatrices(float time, Skeleton skeleton)
        {
            // Create output array
            var matrices = new Matrix4[skeleton.Bones.Length];

            // Get bone transformations
            var transforms = GetTransformsAtTime(time);

            foreach (var root in skeleton.Roots)
            {
                GetAnimationMatrixRecursive(root, Matrix4.Identity, Matrix4.Identity, transforms, ref matrices);
            }

            return matrices;
        }

        private void GetAnimationMatrixRecursive(Bone bone, Matrix4 parentBindPose, Matrix4 parentInvBindPose, Frame transforms, ref Matrix4[] matrices)
        {
            // Calculate world space bind and inverse bind pose
            var bindPose = bone.BindPose * parentBindPose;
            var invBindPose = parentInvBindPose * bone.InverseBindPose;

            // Calculate transformation matrix
            var transform = transforms.Bones[bone.Name];
            var transformMatrix = Matrix4.CreateFromQuaternion(transform.Angle * bone.Angle.Inverted()) * Matrix4.CreateTranslation(transform.Position - bone.Position);

            // Apply tranformation
            var transformed = transformMatrix * bindPose;

            // Store result
            matrices[bone.Index] = invBindPose * transformed;

            // Propagate to childen
            foreach (var child in bone.Children)
            {
                GetAnimationMatrixRecursive(child, transformed, invBindPose, transforms, ref matrices);
            }
        }

        // Get the transformation matrices at a time
        private Frame GetTransformsAtTime(float time)
        {
            // Calculate the index of the current frame
            int frameIndex = (int)(time * Fps * 0.5) % FrameCount;
            float t = (float)((time * Fps * 0.5) - frameIndex) % 1;

            // Get current and next frame
            var frame1 = Frames[frameIndex];
            var frame2 = Frames[(frameIndex + 1) % FrameCount];

            // Create output frame
            var frame = new Frame();

            var length = FrameCount / Fps;

            // Interpolate bone positions and angles
            foreach (var bonePair in frame1.Bones)
            {
                var position = (t * frame2.Bones[bonePair.Key].Position) + ((1 - t) * frame1.Bones[bonePair.Key].Position);
                var angle = (t * frame2.Bones[bonePair.Key].Angle) + ((1 - t) * frame1.Bones[bonePair.Key].Angle);
                angle.Normalize();
                frame.Bones[bonePair.Key] = new FrameBone(position, angle);
            }

            return frame;
        }

        // Construct an animation class from the animation description
        private void ConstructFromDesc(NTROStruct animDesc, NTROStruct decodeKey, string[] decoderArray, NTROArray segmentArray)
        {
            // Get animation properties
            Name = animDesc.Get<string>("m_name");
            Fps = animDesc.Get<float>("fps");

            // Only consider first frame block for now
            var pData = animDesc.Get<NTROArray>("m_pData").Get<NTROStruct>(0);
            var frameBlockArray = pData.Get<NTROArray>("m_frameblockArray").ToArray<NTROStruct>();

            FrameCount = pData.Get<int>("m_nFrames");
            Frames = new Frame[FrameCount];

            // Figure out each frame
            for (var frame = 0; frame < FrameCount; frame++)
            {
                // Create new frame object
                Frames[frame] = new Frame();

                // Read all frame blocks
                foreach (var frameBlock in frameBlockArray)
                {
                    var startFrame = frameBlock.Get<int>("m_nStartFrame");
                    var endFrame = frameBlock.Get<int>("m_nStartFrame");

                    var segmentIndexArray = frameBlock.Get<NTROArray>("m_segmentIndexArray").ToArray<int>();

                    foreach (var segmentIndex in segmentIndexArray)
                    {
                        var segment = segmentArray.Get<NTROStruct>(segmentIndex);
                        ReadSegment(frame - startFrame, segment, decodeKey, decoderArray, ref Frames[frame]);
                    }
                }
            }
        }

        private void ReadSegment(int frame, NTROStruct segment, NTROStruct decodeKey, string[] decoderArray, ref Frame outFrame)
        {
            //Clamp the frame number to be between 0 and the maximum frame
            frame = frame < 0 ? 0 : frame;
            frame = frame >= FrameCount ? FrameCount - 1 : frame;

            var localChannel = segment.Get<int>("m_nLocalChannel");
            var dataChannel = decodeKey.Get<NTROArray>("m_dataChannelArray").Get<NTROStruct>(localChannel);
            var boneNames = dataChannel.Get<NTROArray>("m_szElementNameArray").ToArray<string>();

            var channelAttribute = dataChannel.Get<string>("m_szVariableName");

            // Read container
            var container = segment.Get<NTROArray>("m_container").ToArray<byte>();
            using (var containerReader = new BinaryReader(new MemoryStream(container)))
            {
                var elementIndexArray = dataChannel.Get<NTROArray>("m_nElementIndexArray").ToArray<int>();
                var elementBones = new int[decodeKey.Get<int>("m_nChannelElements")];
                for (int i = 0; i < elementIndexArray.Length; i++)
                {
                    elementBones[elementIndexArray[i]] = i;
                }

                // Read header
                var decoder = decoderArray[containerReader.ReadInt16()];
                var numBlocks = containerReader.ReadInt16();
                var numElements = containerReader.ReadInt16();
                var totalLength = containerReader.ReadInt16();

                // Read bone list
                List<int> elements = new List<int>();
                for (int i = 0; i < numElements; i++)
                {
                    elements.Add(containerReader.ReadInt16());
                }

                // Skip data??
                var size = numBlocks;
                var blocks = numElements;

                switch (decoder)
                {
                    case "CCompressedDeltaVector3":
                    case "CCompressedStaticFullVector3":
                    case "CCompressedStaticVector3":
                    case "CCompressedAnimVector3":
                        blocks = 0;
                        break;
                    case "CCompressedAnimQuaternion":
                        size = 6;
                        break;
                    case "CCompressedFullVector3":
                        size = 12;
                        break;
                }

                containerReader.BaseStream.Position += frame * size * blocks;
                for (int element = 0; element < numElements; element++)
                {
                    //Get the bone we are reading for
                    var bone = elementBones[elements[element]];

                    // Look at the decoder to see what to read
                    switch (decoder)
                    {
                        case "CCompressedStaticFloat":
                            outFrame.SetAttribute(boneNames[bone], channelAttribute, containerReader.ReadSingle());
                            break;
                        case "CCompressedStaticFullVector3":
                        case "CCompressedFullVector3":
                        case "CCompressedDeltaVector3":
                            outFrame.SetAttribute(boneNames[bone], channelAttribute, new OpenTK.Vector3(
                                containerReader.ReadSingle(),
                                containerReader.ReadSingle(),
                                containerReader.ReadSingle()));
                            break;
                        case "CCompressedStaticVector3":
                            outFrame.SetAttribute(boneNames[bone], channelAttribute, new OpenTK.Vector3(
                                ReadHalfFloat(containerReader),
                                ReadHalfFloat(containerReader),
                                ReadHalfFloat(containerReader)));
                            break;
                        case "CCompressedAnimQuaternion":
                            outFrame.SetAttribute(boneNames[bone], channelAttribute, ReadQuaternion(containerReader));
                            break;
#if DEBUG
                        default:
                            Console.WriteLine("Unknown decoder type encountered. Type: " + decoder);
                            break;
#endif
                    }
                }
            }
        }

        // Read a half-precision float from a binary reader
        private float ReadHalfFloat(BinaryReader reader)
        {
            int i = reader.ReadInt16();

            int i1 = i & 0x7fff; // Non-sign bits
            int i2 = i & 0x8000; // Sign
            int i3 = i & 0x7c00; // Exponent

            i1 <<= 13; // Shift significand
            i2 <<= 16; // Shift sign bit;

            i1 += 0x38000000; // Adjust bias
            i1 = i3 == 0 ? 0 : i1; // Denormals as zero
            i1 |= i2; // Add the sign bit again

            return BitConverter.ToSingle(BitConverter.GetBytes(i1), 0);
        }

        //Read and decode encoded quaternion
        private OpenTK.Quaternion ReadQuaternion(BinaryReader reader)
        {
            byte[] bytes = reader.ReadBytes(6);

            // Values
            int i1 = bytes[0] + ((bytes[1] & 63) << 8);
            int i2 = bytes[2] + ((bytes[3] & 63) << 8);
            int i3 = bytes[4] + ((bytes[5] & 63) << 8);

            // Signs
            int s1 = bytes[1] & 128;
            int s2 = bytes[3] & 128;
            int s3 = bytes[5] & 128;

            float c = (float)Math.Sin(Math.PI / 4.0f) / 16384.0f;
            float t1 = (float)Math.Sin(Math.PI / 4.0f);
            float x = (bytes[1] & 64) == 0 ? c * (i1 - 16384) : c * i1;
            float y = (bytes[3] & 64) == 0 ? c * (i2 - 16384) : c * i2;
            float z = (bytes[5] & 64) == 0 ? c * (i3 - 16384) : c * i3;

            float w = (float)Math.Sqrt(1 - (x * x) - (y * y) - (z * z));

            // Apply sign 3
            if (s3 == 128)
            {
                w *= -1;
            }

            // Apply sign 1 and 2
            if (s1 == 128)
            {
                return s2 == 128 ? new OpenTK.Quaternion(y, z, w, x) : new OpenTK.Quaternion(z, w, x, y);
            }
            else
            {
                return s2 == 128 ? new OpenTK.Quaternion(w, x, y, z) : new OpenTK.Quaternion(x, y, z, w);
            }
        }

        // Transform the decoder array to a mapping of index to type ID
        private string[] MakeDecoderArray(NTROArray decoderArray)
        {
            var array = new string[decoderArray.Count];
            for (int i = 0; i < decoderArray.Count; i++)
            {
                var decoder = decoderArray.Get<NTROStruct>(i);
                array[i] = decoder.Get<string>("m_szName");
            }

            return array;
        }

        // Flatten an array of matrices to an array of floats
        private float[] Flatten(Matrix4[] matrices)
        {
            float[] returnArray = new float[matrices.Length * 16];

            for (int i = 0; i < matrices.Length; i++)
            {
                Matrix4 mat = matrices[i];
                returnArray[i * 16] = mat.M11;
                returnArray[(i * 16) + 1] = mat.M12;
                returnArray[(i * 16) + 2] = mat.M13;
                returnArray[(i * 16) + 3] = mat.M14;

                returnArray[(i * 16) + 4] = mat.M21;
                returnArray[(i * 16) + 5] = mat.M22;
                returnArray[(i * 16) + 6] = mat.M23;
                returnArray[(i * 16) + 7] = mat.M24;

                returnArray[(i * 16) + 8] = mat.M31;
                returnArray[(i * 16) + 9] = mat.M32;
                returnArray[(i * 16) + 10] = mat.M33;
                returnArray[(i * 16) + 11] = mat.M34;

                returnArray[(i * 16) + 12] = mat.M41;
                returnArray[(i * 16) + 13] = mat.M42;
                returnArray[(i * 16) + 14] = mat.M43;
                returnArray[(i * 16) + 15] = mat.M44;
            }

            return returnArray;
        }
    }

    internal class Frame
    {
        public Dictionary<string, FrameBone> Bones { get; set; }

        public Frame()
        {
            Bones = new Dictionary<string, FrameBone>();
        }

        public void SetAttribute(string bone, string attribute, object data)
        {
            switch (attribute)
            {
                case "Position":
                    InsertIfUnknown(bone);
                    Bones[bone].Position = (OpenTK.Vector3)data;
                    break;
                case "Angle":
                    InsertIfUnknown(bone);
                    Bones[bone].Angle = (OpenTK.Quaternion)data;
                    break;
                case "data":
                    //ignore
                    break;
#if DEBUG
                default:
                    Console.WriteLine($"Unknown frame attribute '{attribute}' encountered");
                    break;
#endif
            }
        }

        private void InsertIfUnknown(string name)
        {
            if (!Bones.ContainsKey(name))
            {
                Bones[name] = new FrameBone(OpenTK.Vector3.Zero, OpenTK.Quaternion.Identity);
            }
        }
    }

    internal class FrameBone
    {
        public OpenTK.Vector3 Position { get; set; }
        public OpenTK.Quaternion Angle { get; set; }

        public FrameBone(OpenTK.Vector3 pos, OpenTK.Quaternion a)
        {
            Position = pos;
            Angle = a;
        }
    }
}
