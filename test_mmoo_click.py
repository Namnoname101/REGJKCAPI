import sys
import io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')

from selenium import webdriver
from bs4 import BeautifulSoup
import time

options = webdriver.ChromeOptions()
options.add_argument('--headless')
options.add_argument('--disable-gpu')
driver = webdriver.Chrome(options=options)
driver.get('https://m.mmoo.team/?r=QWP70O&app=1')
time.sleep(10)

try:
    reg_btn = driver.find_element(webdriver.common.by.By.CSS_SELECTOR, "button[ng-click*='openRegisterModal']")
    driver.execute_script('arguments[0].click();', reg_btn)
    time.sleep(5)
except Exception as e:
    print('Click failed:', e)

soup = BeautifulSoup(driver.page_source, 'html.parser')
inputs = soup.find_all('input')
for i in inputs:
    print('input:', i.attrs)

buttons = soup.find_all('button')
for b in buttons:
    print('btn:', b.attrs, b.text.strip()[:20])

driver.quit()
