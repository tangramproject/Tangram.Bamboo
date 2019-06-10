﻿// Cypher (c) by Tangram Inc
//
// Cypher is licensed under a
// Creative Commons Attribution-NonCommercial-NoDerivatives 4.0 International License.
//
// You should have received a copy of the license along with this
// work. If not, see <http://creativecommons.org/licenses/by-nc-nd/4.0/>.

using System;
using System.Security;
using TangramCypher.Helper;
using TangramCypher.Helper.LibSodium;
using TangramCypher.ApplicationLayer.Actor;
using Newtonsoft.Json;
using Secp256k1_ZKP.Net;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TangramCypher.ApplicationLayer.Wallet;
using Dawn;
using Microsoft.Extensions.Logging;
using TangramCypher.Model;
using System.Threading.Tasks;

namespace TangramCypher.ApplicationLayer.Coin
{
    public class CoinService : ICoinService
    {
        private readonly IUnitOfWork unitOfWork;
        private readonly ILogger logger;

        public CoinService(IUnitOfWork unitOfWork, ILogger logger)
        {
            this.unitOfWork = unitOfWork;
            this.logger = logger;
        }

        /// <summary>
        /// Builds the receiver.
        /// </summary>
        /// <returns>The receiver.</returns>
        public TaskResult<bool> Receiver(SecureString secret, ulong input, out CoinDto coin, out byte[] blind)
        {

            byte[] blindSum = new byte[32];
            byte[] commitPos = new byte[33];
            byte[] commitSum = new byte[33];

            using (var secp256k1 = new Secp256k1())
            using (var pedersen = new Pedersen())
            using (var rangeProof = new RangeProof())
            {
                coin = MakeSingleCoin(secret, NewStamp(), -1);
                blind = DeriveKey(input, coin.Stamp, coin.Version, secret);

                try
                {
                    blindSum = pedersen.BlindSum(new List<byte[]> { blind }, new List<byte[]> { });
                    commitPos = pedersen.Commit(input, blind);
                    commitSum = pedersen.CommitSum(new List<byte[]> { commitPos }, new List<byte[]> { });

                    AttachEnvelope(blindSum, commitSum, input, secret, ref coin);

                }
                catch (Exception ex)
                {
                    logger.LogError($"Message: {ex.Message}\n Stack: {ex.StackTrace}");
                    return TaskResult<bool>.CreateFailure(ex);
                }
            }

            return TaskResult<bool>.CreateSuccess(true);
        }

        /// <summary>
        /// Builds the sender.
        /// </summary>
        /// <returns>The sender.</returns>
        public async Task<TaskResult<CoinDto>> Sender(Session session, PurchaseDto purchase)
        {
            CoinDto coin = null;
            byte[] blindSum = new byte[32];
            byte[] commitSum = new byte[33];

            using (var secp256k1 = new Secp256k1())
            using (var pedersen = new Pedersen())
            using (var rangeProof = new RangeProof())
            {
                coin = MakeSingleCoin(session.MasterKey, purchase.Stamp, purchase.Version);

                try
                {
                    //TODO: Refactor signature to handle lambda expressions..
                    var txnsAll = await unitOfWork.GetTransactionRepository().All(session);
                    var txns = txnsAll.Result.Where(tx => purchase.Chain.Any(id => id == Guid.Parse(tx.TransactionId)));

                    var received = txns.FirstOrDefault(tx => tx.TransactionType == TransactionType.Receive);

                    var blindNeg = DeriveKey(purchase.Input, received.Stamp, coin.Version, session.MasterKey);
                    var commitNeg = pedersen.Commit(purchase.Input, blindNeg);

                    var commitNegs = txns
                                      .Where(tx => tx.TransactionType == TransactionType.Send)
                                      .Select(c => pedersen.Commit(c.Amount, DeriveKey(c.Amount, c.Stamp, c.Version, session.MasterKey))).ToList();

                    commitNegs.Add(commitNeg);

                    var blindNegSums = txns
                                        .Where(tx => tx.TransactionType == TransactionType.Send)
                                        .Select(c => DeriveKey(c.Amount, c.Stamp, c.Version, session.MasterKey)).ToList();

                    blindNegSums.Add(blindNeg);

                    blindSum = pedersen.BlindSum(new List<byte[]> { received.Blind.FromHex() }, blindNegSums);
                    commitSum = pedersen.CommitSum(new List<byte[]> { received.Commitment.FromHex() }, commitNegs);

                    AttachEnvelope(blindSum, commitSum, purchase.Output, session.MasterKey, ref coin);
                }
                catch (Exception ex)
                {
                    logger.LogError($"Message: {ex.Message}\n Stack: {ex.StackTrace}");
                    return TaskResult<CoinDto>.CreateFailure(ex);
                }
            }

            return TaskResult<CoinDto>.CreateSuccess(coin);
        }

        /// <summary>
        /// Derives the coin.
        /// </summary>
        /// <returns>The coin.</returns>
        /// <param name="coin">Coin.</param>
        public CoinDto DeriveCoin(CoinDto coin, SecureString secret)
        {
            Guard.Argument(secret, nameof(secret)).NotNull();
            Guard.Argument(coin, nameof(coin)).NotNull();

            var v0 = +coin.Version;
            var v1 = +coin.Version + 1;
            var v2 = +coin.Version + 2;

            var c = new CoinDto()
            {
                Keeper = DeriveKey(v1, coin.Stamp, DeriveKey(v2, coin.Stamp, DeriveKey(v2, coin.Stamp, secret).ToSecureString()).ToSecureString()),
                Version = v0,
                Principle = DeriveKey(v0, coin.Stamp, secret),
                Stamp = coin.Stamp,
                Envelope = coin.Envelope,
                Hint = DeriveKey(v1, coin.Stamp, DeriveKey(v1, coin.Stamp, secret).ToSecureString())
            };

            return c;
        }

        /// <summary>
        /// Derives the key.
        /// </summary>
        /// <returns>The key.</returns>
        /// <param name="version">Version.</param>
        /// <param name="stamp">Stamp.</param>
        /// <param name="secret">secret.</param>
        /// <param name="bytes">Bytes.</param>
        public string DeriveKey(int version, string stamp, SecureString secret, int bytes = 32)
        {
            Guard.Argument(stamp, nameof(stamp)).NotNull().NotEmpty();
            Guard.Argument(secret, nameof(secret)).NotNull();

            using (var insecureSecret = secret.Insecure())
            {
                return Cryptography.GenericHashNoKey($"{version} {stamp} {insecureSecret.Value}", bytes).ToHex();
            }
        }

        public byte[] DeriveKey(ulong amount, string stamp, int version, SecureString secret)
        {
            Guard.Argument(amount, nameof(amount)).NotNegative();
            Guard.Argument(secret, nameof(secret)).NotNull();

            using (var insecureSecret = secret.Insecure())
            {
                return Cryptography.GenericHashNoKey($"{amount} {stamp} {version} {insecureSecret.Value}", 32);
            }
        }

        /// <summary>
        /// Hash the specified coin.
        /// </summary>
        /// <returns>The hash.</returns>
        /// <param name="coin">Coin.</param>
        public byte[] Hash(CoinDto coin)
        {
            Guard.Argument(coin, nameof(coin)).NotNull();

            return Cryptography.GenericHashNoKey(
                string.Format("{0} {1} {2} {3} {4} {5} {6} {7} {8}",
                    coin.Envelope.Commitment,
                    coin.Envelope.Proof,
                    coin.Envelope.PublicKey,
                    coin.Envelope.RangeProof,
                    coin.Envelope.Signature,
                    coin.Hint,
                    coin.Keeper,
                    coin.Principle,
                    coin.Stamp));
        }

        /// <summary>
        /// Partial release one secret key for escrow.
        /// </summary>
        /// <returns>The release.</returns>
        /// <param name="version">Version.</param>
        /// <param name="stamp">Stamp.</param>
        /// <param name="memo">Memo.</param>
        /// <param name="secret">secret.</param>
        public string PartialRelease(int version, string stamp, string memo, SecureString secret)
        {
            Guard.Argument(stamp, nameof(stamp)).NotNull().NotEmpty();
            Guard.Argument(secret, nameof(secret)).NotNull();

            var subKey1 = DeriveKey(version + 1, stamp, secret);
            var subKey2 = DeriveKey(version + 2, stamp, secret).ToSecureString();
            var mix = DeriveKey(version + 2, stamp, subKey2);
            var redemption = new RedemptionKeyDto() { Key1 = subKey1, Key2 = mix, Memo = memo, Stamp = stamp };

            return JsonConvert.SerializeObject(redemption);
        }

        /// <summary>
        /// Change ownership.
        /// </summary>
        /// <returns>The swap.</returns>
        /// <param name="secret">secret.</param>
        /// <param name="coin">Coin.</param>
        /// <param name="redemptionKey">Redemption key.</param>
        public (CoinDto, CoinDto) CoinSwap(SecureString secret, CoinDto coin, RedemptionKeyDto redemptionKey)
        {
            Guard.Argument(secret, nameof(secret)).NotNull();
            Guard.Argument(coin, nameof(coin)).NotNull();
            Guard.Argument(redemptionKey, nameof(redemptionKey)).NotNull();

            try
            { coin = coin.FormatCoinFromBase64(); }
            catch (FormatException) { }

            if (!redemptionKey.Stamp.Equals(coin.Stamp))
                throw new Exception("Redemption stamp is not equal to the coins stamp!");

            var v1 = coin.Version + 1;
            var v2 = coin.Version + 2;
            var v3 = coin.Version + 3;
            var v4 = coin.Version + 4;

            var c1 = new CoinDto()
            {
                Keeper = DeriveKey(v2, redemptionKey.Stamp, DeriveKey(v3, redemptionKey.Stamp, DeriveKey(v3, redemptionKey.Stamp, secret).ToSecureString()).ToSecureString()),
                Version = v1,
                Principle = redemptionKey.Key1,
                Stamp = redemptionKey.Stamp,
                Envelope = coin.Envelope,
                Hint = DeriveKey(v2, redemptionKey.Stamp, redemptionKey.Key2.ToSecureString())
            };

            c1.Hash = Hash(c1).ToHex();

            var c2 = new CoinDto()
            {
                Keeper = DeriveKey(v3, redemptionKey.Stamp, DeriveKey(v4, redemptionKey.Stamp, DeriveKey(v4, redemptionKey.Stamp, secret).ToSecureString()).ToSecureString()),
                Version = v2,
                Principle = redemptionKey.Key2,
                Stamp = redemptionKey.Stamp,
                Envelope = coin.Envelope,
                Hint = DeriveKey(v3, redemptionKey.Stamp, DeriveKey(v3, redemptionKey.Stamp, secret).ToSecureString())
            };

            c2.Hash = Hash(c2).ToHex();

            return (c1, c2);
        }

        /// <summary>
        /// Change partial ownership.
        /// </summary>
        /// <returns>The partial one.</returns>
        /// <param name="secret">secret.</param>
        /// <param name="coin">Coin.</param>
        /// <param name="redemptionKey">Redemption key.</param>
        public CoinDto SwapPartialOne(SecureString secret, CoinDto coin, RedemptionKeyDto redemptionKey)
        {
            Guard.Argument(secret, nameof(secret)).NotNull();
            Guard.Argument(coin, nameof(coin)).NotNull();
            Guard.Argument(redemptionKey, nameof(redemptionKey)).NotNull();

            var v1 = coin.Version + 1;
            var v2 = coin.Version + 2;
            var v3 = coin.Version + 3;

            coin.Keeper = DeriveKey(v2, coin.Stamp, DeriveKey(v3, coin.Stamp, DeriveKey(v3, coin.Stamp, secret).ToSecureString()).ToSecureString());
            coin.Version = v1;
            coin.Principle = redemptionKey.Key1;
            coin.Stamp = coin.Stamp;
            coin.Envelope = coin.Envelope;
            coin.Hint = redemptionKey.Key2;

            return coin;
        }

        /// <summary>
        /// Sign the specified amount, version, stamp, secret and msg.
        /// </summary>
        /// <returns>The sign.</returns>
        /// <param name="amount">Amount.</param>
        /// <param name="version">Version.</param>
        /// <param name="stamp">Stamp.</param>
        /// <param name="secret">secret.</param>
        /// <param name="msg">Message.</param>
        public byte[] Sign(ulong amount, int version, string stamp, SecureString secret, byte[] msg)
        {
            Guard.Argument(stamp, nameof(stamp)).NotNull().NotEmpty();
            Guard.Argument(secret, nameof(secret)).NotNull();
            Guard.Argument(msg, nameof(msg)).NotNull().MaxCount(32);

            using (var secp256k1 = new Secp256k1())
            {
                var blind = DeriveKey(version, stamp, secret).FromHex();
                var sig = secp256k1.Sign(msg, blind);

                return sig;
            }
        }

        /// <summary>
        /// Signs with blinding factor.
        /// </summary>
        /// <returns>The with blinding.</returns>
        /// <param name="msg">Message.</param>
        /// <param name="blinding">Blinding.</param>
        public byte[] SignWithBlinding(byte[] msg, byte[] blinding)
        {
            Guard.Argument(msg, nameof(msg)).NotNull().MaxCount(32);
            Guard.Argument(blinding, nameof(blinding)).NotNull().MaxCount(32);

            using (var secp256k1 = new Secp256k1())
            {
                var msgHash = Cryptography.GenericHashNoKey(Encoding.UTF8.GetString(msg));
                return secp256k1.Sign(msgHash, blinding);
            }
        }

        //TODO.. possibly remove?
        /// <summary>
        /// Split coin.
        /// </summary>
        /// <returns>The split.</returns>
        /// <param name="blinding">Blinding.</param>
        public (byte[], byte[]) Split(byte[] blinding, SecureString secret, string stamp, int version)
        {
            Guard.Argument(blinding, nameof(blinding)).NotNull().MaxCount(32);

            using (var pedersen = new Pedersen())
            {
                var skey1 = DeriveKey(0, stamp, version, secret);

                byte[] skey2 = new byte[32];

                try
                {
                    skey2 = pedersen.BlindSum(new List<byte[]> { blinding }, new List<byte[]> { skey1 });
                }
                catch (Exception ex)
                {
                    logger.LogError($"Message: {ex.Message}\n Stack: {ex.StackTrace}");
                    throw ex;
                }

                return (skey1, skey2);
            }
        }

        /// <summary>
        /// Makes the single coin.
        /// </summary>
        /// <returns>The single coin.</returns>
        public CoinDto MakeSingleCoin(SecureString secret, string stamp, int version)
        {
            Guard.Argument(secret, nameof(secret)).NotNull();
            Guard.Argument(stamp, nameof(stamp)).NotNull().NotEmpty();

            return DeriveCoin(new CoinDto
            {
                Version = version + 1,
                Stamp = stamp,
                Envelope = new EnvelopeDto()
            }, secret);
        }

        /// <summary>
        /// Verifies the coin on ownership.
        /// </summary>
        /// <returns>The coin.</returns>
        /// <param name="terminal">Terminal.</param>
        /// <param name="current">Current.</param>
        public int VerifyCoin(CoinDto terminal, CoinDto current)
        {
            Guard.Argument(terminal, nameof(terminal)).NotNull();
            Guard.Argument(current, nameof(current)).NotNull();

            return terminal.Keeper.Equals(current.Keeper) && terminal.Hint.Equals(current.Hint)
               ? 1
               : terminal.Hint.Equals(current.Hint)
               ? 2
               : terminal.Keeper.Equals(current.Keeper)
               ? 3
               : 4;
        }

        /// <summary>
        ///  Releases two secret keys to continue hashchaing for sender/recipient.
        /// </summary>
        /// <returns>The release.</returns>
        /// <param name="version">Version.</param>
        /// <param name="stamp">Stamp.</param>
        /// <param name="secret">secret.</param>
        public (string, string) HotRelease(int version, string stamp, SecureString secret)
        {
            Guard.Argument(stamp, nameof(stamp)).NotNull().NotEmpty();
            Guard.Argument(secret, nameof(secret)).NotNull();

            var key1 = DeriveKey(version + 1, stamp, secret);
            var key2 = DeriveKey(version + 2, stamp, secret);

            return (key1, key2);
        }

        /// <summary>
        /// Attaches the envelope.
        /// </summary>
        /// <param name="secp256k1">Secp256k1.</param>
        /// <param name="pedersen">Pedersen.</param>
        /// <param name="rangeProof">Range proof.</param>
        /// <param name="blindSum">Blind sum.</param>
        /// <param name="commitSum">Commit sum.</param>
        /// <param name="secret">Secret.</param>
        private void AttachEnvelope(byte[] blindSum, byte[] commitSum, ulong balance, SecureString secret, ref CoinDto coin)
        {
            var (k1, k2) = Split(blindSum, secret, coin.Stamp, coin.Version);

            using (var secp256k1 = new Secp256k1())
            using (var pedersen = new Pedersen())
            using (var rangeProof = new RangeProof())
            {
                coin.Envelope.Commitment = commitSum.ToHex();
                coin.Envelope.Proof = k2.ToHex();
                coin.Envelope.PublicKey = pedersen.ToPublicKey(pedersen.Commit(0, k1)).ToHex();
                coin.Hash = Hash(coin).ToHex();
                coin.Envelope.Signature = secp256k1.Sign(coin.Hash.FromHex(), k1).ToHex();

                var proofStruct = rangeProof.Proof(0, balance, blindSum, commitSum, coin.Hash.FromHex());
                var isVerified = rangeProof.Verify(commitSum, proofStruct);

                if (!isVerified)
                    throw new ArgumentOutOfRangeException(nameof(isVerified), "Range proof failed.");

                coin.Envelope.RangeProof = proofStruct.proof.ToHex();
            }
        }

        /// <summary>
        /// Gets the new stamp.
        /// </summary>
        /// <returns>The new stamp.</returns>
        private string NewStamp()
        {
            return Cryptography.GenericHashNoKey(Cryptography.RandomKey()).ToHex();
        }
    }
}
