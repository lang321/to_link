'use strict';
const fs = require('node:fs');
const c = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
console.log('服务名：' + process.argv[3]);
console.log('浏览器地址：\n' + c.origins.map(o => '  ' + o).join('\n'));
console.log('Windows 客户端填写上述服务器地址，并填写以下注册密钥：\n' + c.enrollmentToken);
console.log('设备文件：' + c.stateFile);
console.log('注册密钥用于新设备注册；浏览器登录使用设备 ID 和设备密码。');
