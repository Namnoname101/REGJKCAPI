import sys
import io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')

from selenium import webdriver
from selenium.webdriver.common.by import By
from selenium.webdriver.support.ui import WebDriverWait
from selenium.webdriver.support import expected_conditions as EC

options = webdriver.ChromeOptions()
options.add_argument('--headless')
options.add_argument('--disable-gpu')
options.add_argument('--window-size=375,812')
driver = webdriver.Chrome(options=options)
driver.get('https://m.mmoo.team/?r=QWP70O&app=1')

try:
    WebDriverWait(driver, 15).until(EC.presence_of_element_located((By.TAG_NAME, 'input')))
except Exception as e:
    print('Wait failed:', e)

inputs = driver.find_elements(By.TAG_NAME, 'input')
print(f'Found {len(inputs)} inputs.')
for i in inputs:
    try:
        print(f'type: {i.get_attribute("type")}, name: {i.get_attribute("name")}, placeholder: {i.get_attribute("placeholder")}, class: {i.get_attribute("class")}')
    except:
        pass

buttons = driver.find_elements(By.CSS_SELECTOR, "button, a, div[class*='btn'], div[class*='register']")
for b in buttons:
    try:
        t = b.text.strip().replace('\n', ' ')
        if 'Đăng' in t or 'register' in b.get_attribute('class').lower():
            print(f'Button tag: {b.tag_name}, text: {t}, class: {b.get_attribute("class")}')
    except:
        pass

driver.quit()
