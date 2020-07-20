// Core (c) by Tangram Inc
// 
// Core is licensed under a
// Creative Commons Attribution-NonCommercial-NoDerivatives 4.0 International License.
// 
// You should have received a copy of the license along with this
// work. If not, see <http://creativecommons.org/licenses/by-nc-nd/4.0/>.

using LiteDB;

namespace Tangram.Core.Model
{
    public class KeySet
    {
        public string ChainCode { get; set; }
        public string[] Paths { get; set; }
        public string RootKey { get; set; }
        [BsonId]
        public string StealthAddress { get; set; }
    }
}