// SmartTax TEST SCAFFOLDING for a local rig - not part of the shipped mod (it sits outside the project folder, so it is never built
// into the dll). Copy to the test server's Mods/UserCode/SmartTaxRig/ and run from the web API:
//   POST /api/v1/command/exec {"command":"smarttaxrig setup"}, then {"command":"smarttaxrig tick"}, then {"command":"smarttaxrig check"}
// setup: a fresh backed currency; the first user (A) gets 1000 and the third (C) 500. On A's tax card it records a tax of 100 and a
//   transfer of 10, both to the second user (B), and a payment of 30 from C.
// tick: runs SmartTax's TickAll now (its timer only does so every TickInterval, 300 s by default), which settles them through
//   TransferCompat, the code path that broke on Eco 0.14.2.
// check: expect A 920, B 110, C 470 and nothing left owed; on 0.14.2 the ledger entries carry Tax (tax id RigTax), GovernmentTransfer
//   and FundsAllocation.
// UserCode can't reference a dll mod, so SmartTax is reached by reflection.

namespace Eco.Mods.SmartTaxRig
{
    using System;
    using System.Collections;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using Eco.Gameplay.Economy;
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Systems.Chat;
    using Eco.Gameplay.Systems.Messaging.Chat.Commands;
    using Eco.Shared.Items;
    using Eco.Shared.Utils;

    [ChatCommandHandler]
    public static class SmartTaxRigCommands
    {
        static Currency currency;
        static User a, b, c;

        [ChatCommand("SmartTax test: 'setup' records a tax, a transfer and a payment; 'check' reports what was settled.")]
        public static void SmartTaxRig(IChatClient client, string mode = "check")
        {
            var cardType = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType("Eco.Mods.SmartTax.TaxCard")).FirstOrDefault(t => t != null);
            if (cardType == null) { client.MsgLocStr("SmartTax isn't loaded."); return; }
            var plugin = cardType.Assembly.GetName();
            if (mode == "setup")
            {
                var users = UserManager.Users.OrderBy(u => u.Name, StringComparer.Ordinal).ToList();
                if (users.Count < 3) { client.MsgLocStr($"Needs 3 users, this world has {users.Count}."); return; }
                (a, b, c) = (users[0], users[1], users[2]);
                currency = CurrencyManager.AddCurrency(a, $"RigTax{RandomUtil.Range(1000, 9999)}", CurrencyType.Backed);
                a.BankAccount.AddCurrency(currency, 1000);
                c.BankAccount.AddCurrency(currency, 500);
                var card = cardType.GetMethod("GetOrCreateForUser").Invoke(null, new object[] { a });
                //Start from an empty card: entries left by an earlier run whose world was never saved point at currencies that no longer exist.
                var cleared = 0;
                foreach (var list in new[] { "TaxDebts", "TaxRebates", "PaymentCredits" }.Select(n => cardType.GetProperty(n).GetValue(card)))
                {
                    cleared += (int)list.GetType().GetProperty("Count").GetValue(list);
                    list.GetType().GetMethod("Clear").Invoke(list, null);
                }
                if (cleared > 0) client.MsgLocStr($"Cleared {cleared} leftover entries from A's tax card.");
                cardType.GetMethod("RecordTax").Invoke(card, new object[] { null, b.BankAccount, currency, "RigTax", 100f, false, false });
                cardType.GetMethod("RecordTax").Invoke(card, new object[] { null, b.BankAccount, currency, "RigTransfer", 10f, false, true });
                cardType.GetMethod("RecordPayment").Invoke(card, new object[] { null, c.BankAccount, currency, "RigPay", 30f });
                client.MsgLocStr($"SmartTax {plugin.Version}: recorded for A={a.Name} B={b.Name} C={c.Name} in {currency.Name}");
                return;
            }
            if (mode == "tick")
            {
                //SmartTax ticks every TickInterval seconds (300 by default); this runs the same TickAll the timer does, right now.
                var pluginType = cardType.Assembly.GetType("Eco.Mods.SmartTax.SmartTaxPlugin");
                var obj = pluginType.GetProperty("Obj", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy).GetValue(null);
                pluginType.GetMethod("TickAll").Invoke(obj, null);
                client.MsgLocStr("Ticked all tax cards.");
                return;
            }
            if (currency == null) { client.MsgLocStr("Run 'smarttaxrig setup' first."); return; }
            var sb = new StringBuilder($"SmartTax {plugin.Version}: ");
            sb.Append($"A {a.BankAccount.GetCurrencyHoldingVal(currency)} B {b.BankAccount.GetCurrencyHoldingVal(currency)} C {c.BankAccount.GetCurrencyHoldingVal(currency)}; ");
            var cardOfA = cardType.GetMethod("GetOrCreateForUser").Invoke(null, new object[] { a });
            sb.Append($"still owed: {cardType.GetMethod("DescribeDebts").Invoke(cardOfA, null)} / {cardType.GetMethod("DescribePayments").Invoke(cardOfA, null)}; ");
            sb.Append("ledger of A: ");
            foreach (var tx in Ledger(a.BankAccount).Where(tx => tx.GetType().GetProperty("Currency")?.GetValue(tx) == currency).TakeLast(4))
            {
                string Prop(string name) => tx.GetType().GetProperty(name)?.GetValue(tx)?.ToString() ?? "n/a";
                sb.Append($"[{Prop("Amount")} '{Prop("Description")}' type {Prop("TransferType")} taxId '{Prop("TaxId")}' settlement {Prop("TaxSettlementId")}] ");
            }
            client.MsgLocStr(sb.ToString());
        }

        static object[] Ledger(BankAccount account)
        {
            var ledger = account.GetType().GetProperty("Ledger")?.GetValue(account);
            return ledger?.GetType().GetProperty("Transactions")?.GetValue(ledger) is IEnumerable txs ? txs.Cast<object>().ToArray() : Array.Empty<object>();
        }
    }
}
