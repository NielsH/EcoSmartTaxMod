namespace Eco.Mods.SmartTax
{
    using System;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.ExceptionServices;

    using Gameplay.Economy;
    using Gameplay.Economy.Transfer;
    using Gameplay.Economy.Transfer.Internal;
    using Gameplay.GameActions;
    using Gameplay.Settlements;

    using Shared.Localization;

    /// <summary>
    /// Calls TransferInternalUtils.TransferInternal on whichever Eco 0.14 version is running.
    /// Eco 0.14.2 added a required TransferType parameter, plus optional fields for its economy journal, so the 0.14.1 signature
    /// this mod is compiled against doesn't exist there and a direct call throws MissingMethodException. The method is found by
    /// reflection once and its parameters are filled by name, so each version gets exactly the arguments it takes.
    /// </summary>
    public static class TransferCompat
    {
        private static readonly MethodInfo transferMethod;
        private static readonly ParameterInfo[] transferParameters;

        // Parameters this mod knows how to fill. Anything else must be optional, or the method is refused.
        private static readonly string[] knownParameters = { "pack", "amount", "currency", "sourceAccount", "targetAccount", "sender", "transferDesc", "pendingChanges", "transferType", "taxId", "taxSettlementId" };

        // The TransferType names this mod records, matching vanilla's own law actions: a tax is Tax, a payment from government
        // funds is FundsAllocation (like Pay), and a law-mandated transfer is GovernmentTransfer (like Transfer To Account).
        public const string Tax = "Tax", FundsAllocation = "FundsAllocation", GovernmentTransfer = "GovernmentTransfer";

        static TransferCompat()
        {
            transferMethod = typeof(TransferInternalUtils)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == nameof(TransferInternalUtils.TransferInternal))
                .Where(m => m.GetParameters().All(p => knownParameters.Contains(p.Name) || p.HasDefaultValue))
                .OrderByDescending(m => m.GetParameters().Length)
                .FirstOrDefault()
                ?? throw new MissingMethodException($"No usable {nameof(TransferInternalUtils)}.{nameof(TransferInternalUtils.TransferInternal)} found in this Eco version.");
            transferParameters = transferMethod.GetParameters();

            var transferTypeParameter = transferParameters.FirstOrDefault(p => p.Name == "transferType");
            var unknown = transferTypeParameter == null ? Array.Empty<string>() : new[] { Tax, FundsAllocation, GovernmentTransfer }.Where(n => !Enum.IsDefined(transferTypeParameter.ParameterType, n)).ToArray();
            if (unknown.Length > 0) { throw new MissingMemberException($"{transferTypeParameter.ParameterType.Name} has no {string.Join(", ", unknown)} in this Eco version."); }
        }

        /// <summary>The signature in use, for the startup log.</summary>
        public static string Signature => $"{nameof(TransferInternalUtils.TransferInternal)}({string.Join(", ", transferParameters.Select(p => p.Name))})";

        /// <summary>
        /// Queues a transfer on the pack, as TransferInternalUtils.TransferInternal does.
        /// </summary>
        /// <param name="transferType">Name of the Eco TransferType recorded on the ledger and economy journal (0.14.2+; ignored before).</param>
        /// <param name="taxId">Which tax this is, for the journal's tax breakdown; null for anything that isn't a tax.</param>
        /// <param name="taxSettlement">The settlement levying the tax, if any.</param>
        public static void Transfer(GameActionPack pack, float amount, Currency currency, BankAccount sourceAccount, BankAccount targetAccount, LocString transferDesc, AccountChangeSet pendingChanges, string transferType, string taxId = null, Settlement taxSettlement = null)
        {
            var args = new object[transferParameters.Length];
            for (int i = 0; i < transferParameters.Length; ++i)
            {
                var parameter = transferParameters[i];
                args[i] = parameter.Name switch
                {
                    "pack" => pack,
                    "amount" => amount,
                    "currency" => currency,
                    "sourceAccount" => sourceAccount,
                    "targetAccount" => targetAccount,
                    "sender" => null,
                    "transferDesc" => transferDesc,
                    "pendingChanges" => pendingChanges,
                    "transferType" => Enum.Parse(parameter.ParameterType, transferType),
                    "taxId" => taxId,
                    "taxSettlementId" => taxSettlement?.Id ?? 0,
                    _ => parameter.DefaultValue,
                };
            }
            try
            {
                transferMethod.Invoke(null, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            }
        }
    }
}
