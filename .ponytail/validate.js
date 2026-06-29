#!/usr/bin/env node

/**
 * Ponytail Configuration Validator for TinadecOffice
 * Validates that Ponytail rules are properly configured
 */

const fs = require('fs');
const path = require('path');

const CONFIG_FILE = path.join(__dirname, 'config.json');
const RULES_FILE = path.join(__dirname, 'rules.md');

console.log('🔍 Validating Ponytail configuration...\n');

// Check if config file exists
if (!fs.existsSync(CONFIG_FILE)) {
  console.error('❌ Error: .ponytail/config.json not found');
  process.exit(1);
}

// Check if rules file exists
if (!fs.existsSync(RULES_FILE)) {
  console.error('❌ Error: .ponytail/rules.md not found');
  process.exit(1);
}

// Load and validate config
try {
  const config = JSON.parse(fs.readFileSync(CONFIG_FILE, 'utf8'));
  
  console.log('✅ Config file loaded successfully');
  console.log(`   Project: ${config.project}`);
  console.log(`   Mode: ${config.mode}`);
  console.log(`   Version: ${config.version}\n`);
  
  // Validate safety settings
  if (config.safety) {
    console.log('🛡️  Safety settings:');
    console.log(`   Preserve Validation: ${config.safety.preserveValidation ? '✅' : '❌'}`);
    console.log(`   Preserve Error Handling: ${config.safety.preserveErrorHandling ? '✅' : '❌'}`);
    console.log(`   Preserve Security: ${config.safety.preserveSecurity ? '✅' : '❌'}`);
    console.log(`   Preserve Accessibility: ${config.safety.preserveAccessibility ? '✅' : '❌'}`);
    console.log('');
  }
  
  // Validate architecture settings
  if (config.architecture) {
    console.log('🏗️  Architecture layers:');
    Object.entries(config.architecture).forEach(([layer, settings]) => {
      console.log(`   ${layer}: ${settings.framework}`);
      console.log(`     Principles: ${settings.principles.join(', ')}`);
    });
    console.log('');
  }
  
  // Check AGENTS.md for Ponytail rules
  const agentsFile = path.join(__dirname, '..', 'AGENTS.md');
  if (fs.existsSync(agentsFile)) {
    const agentsContent = fs.readFileSync(agentsFile, 'utf8');
    if (agentsContent.includes('PONYTAIL CODING PRINCIPLES')) {
      console.log('✅ AGENTS.md contains Ponytail rules');
    } else {
      console.log('⚠️  Warning: AGENTS.md does not contain Ponytail rules');
    }
  }
  
  // Check CLAUDE.md for Ponytail integration
  const claudeFile = path.join(__dirname, '..', 'CLAUDE.md');
  if (fs.existsSync(claudeFile)) {
    const claudeContent = fs.readFileSync(claudeFile, 'utf8');
    if (claudeContent.includes('Ponytail Integration')) {
      console.log('✅ CLAUDE.md contains Ponytail integration');
    } else {
      console.log('⚠️  Warning: CLAUDE.md does not contain Ponytail integration');
    }
  }
  
  console.log('\n✨ Ponytail configuration validation complete!');
  
} catch (error) {
  console.error('❌ Error reading config file:', error.message);
  process.exit(1);
}
