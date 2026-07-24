import sys
import io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')

from selenium import webdriver
from selenium.webdriver.common.by import By
from selenium.webdriver.support.ui import WebDriverWait
from selenium.webdriver.support import expected_conditions as EC
import time

options = webdriver.ChromeOptions()
options.add_argument('--headless')
options.add_argument('--disable-gpu')
options.add_argument('--window-size=375,812')
driver = webdriver.Chrome(options=options)
driver.get('https://www.88jin88.com/m/register?r=tbk8366')

try:
    WebDriverWait(driver, 15).until(EC.presence_of_element_located((By.TAG_NAME, 'input')))
except Exception as e:
    print('Wait failed:', e)

inputs = driver.find_elements(By.TAG_NAME, 'input')
print(f'Found {len(inputs)} inputs.')
for i in inputs:
    try:
        print(f'type: {i.get_attribute("type")}, name: {i.get_attribute("name")}, placeholder: {i.get_attribute("placeholder")}, class: {i.get_attribute("class")}')
    except Exception as e:
        print('Error getting attr:', e)

driver.quit()
