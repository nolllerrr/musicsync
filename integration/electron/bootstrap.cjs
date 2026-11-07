'use strict';

try {
  require('./main.cjs').install();
} catch (error) {
  console.error('MusicSync Together failed to initialize:', error.message);
}

module.exports = require('../index.js');
