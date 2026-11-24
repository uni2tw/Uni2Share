axios.defaults.headers.common['Authorization'] = '';
axios.defaults.headers.post['Content-Type'] = 'application/json; charset=utf-8';
axios.defaults.withCredentials = true;

var api = {};

api.folder = function (folder) {
    console.log('post /api/folder: ' + folder);
    return axios.post('/api/folder', {
        p: folder
    });
};

api.search = function(keyword) {
    console.log('post /api/search: ' + keyword);
    return axios.post('/api/search', {
        q: keyword
    });
}

api.pathId = function(pathname) {
    console.log('post /api/pathId: ' + pathname);
    return axios.post('/api/pathId', {
        pathname: pathname
    });
}

api.info = function() {
    return axios.get('/api/info');
}

api.favorites = function() {
    return axios.get('/api/favorites');
}

api.favoriteItems = function() {
    return axios.get('/api/favorites/items');
}

// kind: 'album'（資料夾 id）或 'track'（音樂檔 id）
api.setFavorite = function(kind, id, fav) {
    return axios.post('/api/favorites', {
        kind: kind,
        id: id,
        fav: fav
    });
}
// 自訂最愛單曲的順序：ids = 畫面上單曲由上到下的 id
api.reorderFavorites = function(ids) {
    return axios.post('/api/favorites/reorder', {
        ids: ids
    });
}
// 回報聽過一首（聽滿 30 秒或過半才呼叫）；失敗不打擾使用者，呼叫端自行忽略
api.recordPlay = function(id, title, artist, album, duration) {
    return axios.post('/api/plays', {
        id: id,
        title: title || '',
        artist: artist || '',
        album: album || '',
        duration: duration || 0
    });
}
// 聽歌統計；days：7／30／365，0 是全部
api.playStats = function(days) {
    return axios.get('/api/plays/stats', { params: { days: days || 0 } });
}
// 清除自己最愛裡找不到的項目（資料夾已改名或刪除）
api.pruneFavorites = function() {
    return axios.post('/api/favorites/prune', {});
}
// 置頂的資料夾（每人各自，有上限）
api.pins = function() {
    return axios.get('/api/pins');
}
api.setPin = function(id, pin) {
    return axios.post('/api/pin', {
        id: id,
        pin: pin
    });
}
// 動畫評分（0.5 為一級，0 = 未評分）；id 是資料夾 id
api.setRating = function(id, rating) {
    return axios.post('/api/rating', {
        id: id,
        rating: rating
    });
}
// 站內「新上架」：清單（不標已讀）與標示已讀
api.news = function() {
    return axios.get('/api/news');
}

api.mediaInfo = function(id) {
    return axios.get('/api/mediainfo', { params: { id: id } });
}

api.setUnplayable = function(id, value) {
    return axios.post('/api/unplayable', { id: id, value: value });
}

api.coverInfo = function(id) {
    return axios.get('/api/coverinfo', { params: { id: id } });
}

api.coverCandidates = function(id, q, artist) {
    return axios.get('/api/covercandidates', { params: { id: id, q: q, artist: artist || undefined } });
}

api.setCover = function(id, url) {
    return axios.post('/api/coverset', { id: id, url: url });
}

// 管理者儲存專輯介紹（資料夾裡的 README.md）；text 是空白就是清空（刪檔）
api.saveAlbumNote = function(id, text) {
    return axios.post('/api/albumnote', { id: id, text: text });
}

// 會員點 CUE 或它的 FLAC：取得專輯曲目與即時拆軌進度（沒轉過會排進佇列）
api.cueOpen = function(id) {
    return axios.post('/api/cue/open', { id: id });
}

api.transcodeStatus = function(ids, first) {
    return axios.post('/api/transcode/status', { ids: ids, first: first || null });
}

api.newsRead = function() {
    return axios.post('/api/news/read', {});
}

// 關聯推薦（跨季作品）：看同組作品、管理者搜尋與儲存
api.related = function(id) {
    return axios.get('/api/related', { params: { id: id } });
}

api.relatedSearch = function(id, q) {
    return axios.get('/api/related/search', { params: { id: id, q: q } });
}

api.setRelated = function(id, keys) {
    return axios.post('/api/related', { id: id, keys: keys });
}

// 管理者一鍵修正：把動畫資料夾裡不符合播放標準的 MKV 全部排進轉檔
api.convertFix = function(id) {
    return axios.post('/api/convertfix', { id: id });
}
