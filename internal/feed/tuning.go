package feed

import "strings"

// Everything worth changing when adapting the app lives in this file:
// sources, keywords, theaters and limits.

const (
	RefreshHours = 6
	HorizonDays  = 45
	MaxShown     = 40

	// eiga.com lists 20 films per page, newest release first. Ten pages is
	// about three months; anything older simply sorts last.
	releasePages = 10
)

// Source is one listing page. Pop is a bonus for the site's own ranking.
type Source struct {
	URL   string
	Pop   int
	Sakai bool
}

// Both sites embed schema.org Event data in their listing pages.
var Sources = []Source{
	{"https://www.walkerplus.com/event_list/ar0727/", 10, false}, // Osaka prefecture, popular first
	{"https://www.walkerplus.com/event_list/ar0727/2.html", 6, false},
	{"https://www.walkerplus.com/event_list/ar0727/3.html", 3, false},
	{"https://www.walkerplus.com/event_list/weekend/ar0727/", 5, false},
	{"https://www.walkerplus.com/event_list/ar0727140/sakai/", 0, true},
	{"https://www.jalan.net/event/270000/", 4, false}, // Osaka prefecture
	{"https://www.jalan.net/event/270000/page_2/", 2, false},
	{"https://www.jalan.net/event/270000/page_3/", 0, false},
	{"https://www.jalan.net/event/270000/page_4/", 0, false},
	{"https://www.jalan.net/event/maj_271400000/", 0, true}, // Sakai city
}

// Like is a group of keywords that makes an event a better fit.
type Like struct {
	Tag    string
	Weight int
	Words  []string
}

func like(tag string, weight int, words string) Like {
	return Like{tag, weight, strings.Fields(words)}
}

// Ordered by weight: the first group that matches gives the tag.
// Matched against title + description.
var Likes = []Like{
	like("Illumination", 30, "イルミネーション ライトアップ 光の ランタン キャンドル 夜景 プロジェクションマッピング"),
	like("Fireworks", 30, "花火"),
	like("Christmas", 28, "クリスマス Xmas"),
	like("Autumn leaves", 26, "紅葉 もみじ"),
	like("Flowers", 24, "桜 花見 梅まつり 梅林 バラ園 ローズガーデン 薔薇 菊 あじさい 紫陽花 藤まつり コスモス チューリップ ひまわり 庭園 フラワー"),
	like("Food & drink", 22, "グルメ ワイン ビール 日本酒 スイーツ チョコ ブッフェ ビュッフェ 食べ放題 アフタヌーンティー 肉 ラーメン カフェ ベーカリー"),
	like("Market", 20, "マルシェ マーケット 蚤の市 骨董 陶器 クラフト 手作り市 朝市"),
	like("Art", 20, "美術 アート 展覧会 特別展 企画展 写真展 ミュージアム 没入"),
	like("Music", 18, "コンサート ライブ ジャズ 音楽 フェス オーケストラ クラシック"),
	like("Festival", 18, "祭 まつり 縁日 盆踊り 初詣 えびす"),
	like("Outing", 12, "水族館 動物園 プラネタリウム クルーズ 温泉 遊園地 スケート"),
}

var Dislikes = strings.Fields("子ども こども 子供 キッズ 親子 ファミリー 小学生 幼児 ベビー 赤ちゃん ヒーロー プリキュア アンパンマン " +
	"セミナー 講座 講演 説明会 相談会 就職 健康 体操 シニア 競馬 競輪 ボートレース 囲碁 将棋")

// Theater is a TOHO cinema offered in the showtimes view. Code is the number
// in the theater's schedule URL on tohotheater.jp.
type Theater struct {
	Code string `json:"code"`
	Name string `json:"name"`
}

var Theaters = []Theater{{"037", "Umeda"}, {"032", "Namba"}}
