﻿using System.IO;
using System.Collections.Generic;
using System.Linq;

using Phantasma.Cryptography;
using Phantasma.Core;
using Phantasma.Core.Types;
using Phantasma.Storage.Utils;
using Phantasma.Storage;
using Phantasma.Domain;

namespace Phantasma.Blockchain
{
    public sealed class Transaction : ITransaction, ISerializable
    {
        public byte[] Script { get; private set; }

        public string NexusName { get; private set; }
        public string ChainName { get; private set; }

        public Timestamp Expiration { get; private set; }

        public byte[] Payload { get; private set; }

        public Signature[] Signatures { get; private set; }
        public Hash Hash { get; private set; }

        public static Transaction Unserialize(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes))
            {
                using (var reader = new BinaryReader(stream))
                {
                    return Unserialize(reader);
                }
            }
        }

        public static Transaction Unserialize(BinaryReader reader)
        {
            var tx = new Transaction();
            tx.UnserializeData(reader);
            return tx;
        }

        public void Serialize(BinaryWriter writer, bool withSignature)
        {
            using (var stream = new MemoryStream())
            {
                using (var temp = new BinaryWriter(stream))
                {
                    temp.WriteVarString(this.NexusName);
                    temp.WriteVarString(this.ChainName);
                    temp.WriteByteArray(this.Script);
                    temp.Write(this.Expiration.Value);

                    if (withSignature)
                    {
                        temp.WriteVarInt(Signatures.Length);
                        foreach (var signature in this.Signatures)
                        {
                            temp.WriteSignature(signature);
                        }
                    }
                }

                var bytes = stream.ToArray();
                var compressed = Compression.CompressGZip(bytes);
                writer.WriteVarInt(bytes.Length);
                writer.WriteByteArray(compressed);
                writer.WriteByteArray(this.Payload);
            }
        }

        public override string ToString()
        {
            return $"{Hash}";
        }

        // required for deserialization
        public Transaction()
        {

        }

        // transactions are always created unsigned, call Sign() to generate signatures
        public Transaction(string nexusName, string chainName, byte[] script, Timestamp expiration)
        {
            Throw.IfNull(script, nameof(script));

            this.NexusName = nexusName;
            this.ChainName = chainName;
            this.Script = script;
            this.Expiration = expiration;
            this.Payload = new byte[0];

            this.Signatures = new Signature[0];

            this.UpdateHash();
        }

        public byte[] ToByteArray(bool withSignature)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream))
                {
                    Serialize(writer, withSignature);
                }

                return stream.ToArray();
            }
        }

        public bool HasSignatures => Signatures != null && Signatures.Length > 0;

        public void Sign(Signature signature)
        {
            this.Signatures = this.Signatures.Union(new Signature[] { signature }).ToArray();
        }

        public void Sign(KeyPair owner)
        {
            Throw.If(owner == null, "invalid keypair");

            var msg = this.ToByteArray(false);
            var sig = owner.Sign(msg);

            var sigs = new List<Signature>();

            if (this.Signatures != null && this.Signatures.Length > 0)
            {
                sigs.AddRange(this.Signatures);
            }

            sigs.Add(sig);
            this.Signatures = sigs.ToArray();
        }

        public bool IsSignedBy(Address address)
        {
            return IsSignedBy(new Address[] { address });
        }

        public bool IsSignedBy(IEnumerable<Address> addresses)
        {
            if (!HasSignatures)
            {
                return false;
            }

            var msg = this.ToByteArray(false);

            foreach (var signature in this.Signatures)
            {
                if (signature.Verify(msg, addresses))
                {
                    return true;
                }
            }

            return false;
        }

        public bool IsValid(Chain chain)
        {
            return (chain.Name == this.ChainName && chain.Nexus.Name == this.NexusName);
        }

        private void UpdateHash()
        {
            var data = this.ToByteArray(false);
            var hash = CryptoExtensions.SHA256(data);
            this.Hash = new Hash(hash);
        }

        public void SerializeData(BinaryWriter writer)
        {
            this.Serialize(writer, true);
        }

        public void UnserializeData(BinaryReader reader)
        {
            var expectedLen = (int)reader.ReadVarInt();
            var bytes = reader.ReadByteArray();
            this.Payload = reader.ReadByteArray();
            var decompressed = Compression.DecompressGZip(bytes);

            if (decompressed.Length > expectedLen)
            {
                decompressed = decompressed.Take(expectedLen).ToArray();
            }

            using (var stream = new MemoryStream(decompressed))
            {
                using (var temp = new BinaryReader(stream))
                {
                    this.NexusName = temp.ReadVarString();
                    this.ChainName = temp.ReadVarString();
                    this.Script = temp.ReadByteArray();
                    this.Expiration = temp.ReadUInt32();

                    // check if we have some signatures attached
                    try
                    {
                        var signatureCount = (int)temp.ReadVarInt();
                        this.Signatures = new Signature[signatureCount];
                        for (int i = 0; i < signatureCount; i++)
                        {
                            Signatures[i] = temp.ReadSignature();
                        }
                    }
                    catch
                    {
                        this.Signatures = new Signature[0];
                    }
                }
            }

            this.UpdateHash();
        }

        public void Mine(ProofOfWork targetDifficulty)
        {
            Mine((int)targetDifficulty);
        }

        public void Mine(int targetDifficulty)
        {
            Throw.If(targetDifficulty < 0 || targetDifficulty > 256, "invalid difficulty");
            Throw.If(Signatures.Length > 0, "cannot be signed");

            if (targetDifficulty == 0)
            {
                return; // no mining necessary 
            }

            uint nonce = 0;

            while (true)
            {
                if (this.Hash.GetDifficulty() >= targetDifficulty)
                {
                    return;
                }

                if (nonce == 0)
                {
                    this.Payload = new byte[4];
                }

                nonce++;
                if (nonce == 0)
                {
                    throw new ChainException("Transaction mining failed");
                }

                Payload[0] = (byte)((nonce >> 0) & 0xFF);
                Payload[1] = (byte)((nonce >> 8) & 0xFF);
                Payload[2] = (byte)((nonce >> 16) & 0xFF);
                Payload[3] = (byte)((nonce >> 24) & 0xFF);
                UpdateHash();
            }
        }
    }
}
