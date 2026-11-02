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

api.coverCandidates = function(id, q) {
    return axios.get('/api/covercandidates', { params: { id: id, q: q } });
}

api.setCover = function(id, url) {
    return axios.post('/api/coverset', { id: id, url: url });
}

api.transcodeStatus = function(ids, first) {
    return axios.post('/api/transcode/status', { ids: ids, first: first || null });
}

api.newsRead = function() {
    return axios.post('/api/news/read', {});
}
