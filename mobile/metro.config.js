const { getDefaultConfig } = require('expo/metro-config');

const config = getDefaultConfig(__dirname);
// expo-sqlite na web usa wa-sqlite (WASM).
config.resolver.assetExts.push('wasm');

module.exports = config;
