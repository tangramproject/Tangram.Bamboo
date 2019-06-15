// Cypher (c) by Tangram Inc
// 
// Cypher is licensed under a
// Creative Commons Attribution-NonCommercial-NoDerivatives 4.0 International License.
// 
// You should have received a copy of the license along with this
// work. If not, see <http://creativecommons.org/licenses/by-nc-nd/4.0/>.

namespace TangramCypher.Model
{
    public class TrackDto
    {

        [PrimaryKey]
        public string PublicKey { get; set; }
        public int Skip { get; set; }
        public int Take { get; set; }
    }
}