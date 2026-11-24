const path = require('path');
const MiniCssExtractPlugin = require('mini-css-extract-plugin');
const webpack = require('webpack');
module.exports = {
 entry: { Overtake: './src/index.tsx' },
 externalsType: 'window', externals: { react:'React', 'cs2/api':'cs2/api', 'cs2/modding':'cs2/modding', 'cs2/ui':'cs2/ui', 'cs2/input':'cs2/input' },
 resolve: { extensions:['.tsx','.ts','.js'] },
 module: {rules:[{test:/\.tsx?$/,exclude:/node_modules/,use:'ts-loader'},{test:/\.css$/,use:[MiniCssExtractPlugin.loader,'css-loader']}]},
 output:{path:path.resolve(__dirname,'dist'),filename:'[name].mjs',library:{type:'module'},publicPath:'coui://ui-mods/'},
 optimization:{minimize:false},
    experiments:{outputModule:true}, plugins:[new MiniCssExtractPlugin(), new webpack.BannerPlugin({banner:'Cities: Skylines II UI Module\n\nId: Overtake\nAuthor: Overtake\nVersion: 0.1.7\nDependencies: ',entryOnly:true})], stats:'errors-warnings'
};
