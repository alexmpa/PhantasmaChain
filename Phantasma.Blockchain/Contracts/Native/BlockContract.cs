﻿using Phantasma.Core.Types;
using Phantasma.Cryptography;

namespace Phantasma.Blockchain.Contracts.Native
{
    public sealed class BlockContract: SmartContract
    {
        public override string Name => "block";

        public BlockContract() : base()
        {
        }

        public Address GetCurrentValidator()
        {
            Address lastValidator;
            Timestamp validationSlotTime;

            var slotDuration = (int)Runtime.GetGovernanceValue(ValidatorContract.ValidatorRotationTimeTag);
            var chainCreationTime = Runtime.Nexus.GenesisTime;

            if (Runtime.Chain.BlockHeight > 0)
            {
                var lastBlock = Runtime.Chain.LastBlock;
                lastValidator = Runtime.Chain.GetValidatorForBlock(lastBlock);
                validationSlotTime = lastBlock.Timestamp;
            }
            else
            {
                lastValidator = Runtime.Nexus.GetValidatorByIndex(0).address;
                validationSlotTime = chainCreationTime;
            }

            var adjustedSeconds = (uint)((validationSlotTime.Value / slotDuration) * slotDuration);
            validationSlotTime = new Timestamp(adjustedSeconds);


            var diff = Runtime.Time - validationSlotTime;
            if (diff < slotDuration)
            {
                return lastValidator;
            }

            int validatorIndex = (int)(diff / slotDuration);
            var validatorCount = Runtime.Nexus.GetActiveValidatorCount();
            validatorIndex = validatorIndex % validatorCount;

            var currentIndex = validatorIndex;

            do
            {
                var validator = Runtime.Nexus.GetValidatorByIndex(validatorIndex);
                if (validator.status == ValidatorStatus.Active && !validator.address.IsNull)
                {
                    return validator.address;
                }

                validatorIndex++;
                if (validatorIndex >= validatorCount)
                {
                    validatorIndex = 0;
                }
            } while (currentIndex != validatorIndex);

            // should never reached here, failsafe
            return Runtime.Nexus.GenesisAddress;
        }

        public void OpenBlock(Address from)
        {
            Runtime.Expect(IsWitness(from), "witness failed");

            var count = Runtime.Nexus.Ready ? Runtime.Nexus.GetActiveValidatorCount() : 0;
            if (count > 0)
            {
                Runtime.Expect(Runtime.Nexus.IsKnownValidator(from), "validator failed");
                var expectedValidator = GetCurrentValidator();
                Runtime.Expect(from == expectedValidator, "current validator mismatch");
            }
            else
            {
                Runtime.Expect(Runtime.Chain.IsRoot, "must be root chain");
            }

            Runtime.Notify(EventKind.BlockCreate, from, Runtime.Chain.Address);
        }

        public void CloseBlock(Address from)
        {
            var expectedValidator = GetCurrentValidator();
            Runtime.Expect(from == expectedValidator, "current validator mismatch");
            Runtime.Expect(IsWitness(from), "witness failed");

            var validators = Runtime.Nexus.GetValidators();
            Runtime.Expect(validators.Length > 0, "no active validators found");

            var totalAvailable = Runtime.GetBalance(Nexus.FuelTokenSymbol, this.Address);
            var totalValidators = Runtime.Nexus.GetActiveValidatorCount();
            var amountPerValidator = totalAvailable / totalValidators;
            Runtime.Expect(amountPerValidator > 0, "not enough fees available");

            Runtime.Notify(EventKind.BlockClose, from, Runtime.Chain.Address);

            int delivered = 0;
            for (int i = 0; i < totalValidators; i++)
            {
                var validator = validators[i];
                if (validator.status != ValidatorStatus.Active)
                {
                    continue;
                }

                if (Runtime.Nexus.TransferTokens(Runtime, Nexus.FuelTokenSymbol, this.Address, validator.address, amountPerValidator))
                {
                    Runtime.Notify(EventKind.TokenReceive, validator.address, new TokenEventData() { chainAddress = this.Runtime.Chain.Address, value = amountPerValidator, symbol = Nexus.FuelTokenSymbol });
                    delivered++;
                }
            }

            Runtime.Expect(delivered > 0, "failed to claim fees");
        }
    }
}
